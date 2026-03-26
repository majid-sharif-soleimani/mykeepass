/*
 * MyKeePass — Google Drive × KeePass 2 Console Manager
 * ══════════════════════════════════════════════════════
 *
 * SETUP (one-time):
 *   1. Open Google Cloud Console → APIs & Services → Enable "Google Drive API".
 *   2. Create an OAuth 2.0 credential (Application type: Desktop app).
 *   3. Download the JSON → save it as credentials.json next to this executable.
 *      ⚠  NEVER commit credentials.json to source control — add it to .gitignore.
 *   4. Optionally edit appsettings.json to configure accounts and databases.
 *
 * On first run a browser window opens for the Google sign-in flow.
 * The resulting refresh token is cached in %AppData%\MyKeePass so all
 * subsequent runs are fully silent.
 */

using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using mykeepass.Helpers;
using mykeepass.Models;
using mykeepass.Services;
using mykeepass.UI;

// ─── Center the console window on screen ──────────────────────────────────────
ConsoleWindowHelper.CenterOnScreen();

// ─── Startup banner ───────────────────────────────────────────────────────────
Console.WriteLine("══════════════════════════════════════════════");
Console.WriteLine("  MyKeePass — Google Drive KeePass Manager   ");
Console.WriteLine("══════════════════════════════════════════════");
Console.WriteLine();

// ─── Step 1: Load configuration ───────────────────────────────────────────────
AppConfig config = await LoadOrCreateConfigAsync();

// ─── Step 2: Pick which database to open ──────────────────────────────────────
var selection = PickDatabase(config);
if (selection is null) return;
var (account, selectedDb) = selection.Value;

// ─── Step 3: Authenticate with Google Drive ───────────────────────────────────
Console.WriteLine("Authenticating with Google Drive…");
GoogleDriveService driveService;
try
{
    driveService = await GoogleDriveService.CreateAsync("credentials.json", account.Email);
    Console.WriteLine("  ✓ Authenticated.\n");
}
catch (FileNotFoundException)
{
    Console.Error.WriteLine(
        "ERROR: 'credentials.json' not found.\n" +
        "Download it from Google Cloud Console > APIs & Services > Credentials,\n" +
        "choose 'Desktop app', and place the file next to the executable.");
    return;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: Google authentication failed — {ex.Message}");
    return;
}

// ─── Step 4: Find and download the .kdbx file ─────────────────────────────────
Console.WriteLine($"Searching Drive for '{selectedDb.FileName}'…");
MemoryStream dbStream;
string       fileId;
try
{
    var (stream, id) = await driveService.DownloadFileAsync(
        selectedDb.FileName, selectedDb.FolderPath);

    if (stream is null || id is null)
    {
        Console.Error.WriteLine(
            $"ERROR: '{selectedDb.File}' was not found on Google Drive.\n" +
            "Check the file path in appsettings.json.");
        driveService.Dispose();
        return;
    }

    (dbStream, fileId) = (stream, id);
    Console.WriteLine($"  ✓ Downloaded {dbStream.Length:N0} bytes.\n");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: Download failed — {ex.Message}");
    driveService.Dispose();
    return;
}

// ─── Step 4b: Download key file (if configured) ───────────────────────────────
MemoryStream? keyStream = null;
if (selectedDb.KeyFile is not null)
{
    Console.WriteLine($"Searching Drive for key file '{selectedDb.KeyFileName}'…");
    try
    {
        var (ks, _) = await driveService.DownloadFileAsync(
            selectedDb.KeyFileName!, selectedDb.KeyFolderPath);

        if (ks is null)
        {
            Console.Error.WriteLine(
                $"ERROR: Key file '{selectedDb.KeyFile}' was not found on Google Drive.\n" +
                "Check the KeyFile path in appsettings.json.");
            await dbStream.DisposeAsync();
            driveService.Dispose();
            return;
        }

        keyStream = ks;
        Console.WriteLine($"  ✓ Key file downloaded.\n");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: Key file download failed — {ex.Message}");
        await dbStream.DisposeAsync();
        driveService.Dispose();
        return;
    }
}

// ─── Step 5: Unlock the database ──────────────────────────────────────────────
KeePassService keepass;
try
{
    bool helloAvailable = await WindowsHelloService.IsAvailableAsync();
    KeePassService? ks  = null;

    // ── 5a: Windows Hello path ────────────────────────────────────────────────
    if (helloAvailable && WindowsHelloService.HasStoredPassword(account.Email, selectedDb.FileName))
    {
        Console.Write("Windows Hello is available. Use it? [Y/n]: ");
        string? helloChoice = Console.ReadLine();
        bool useHello = string.IsNullOrWhiteSpace(helloChoice) ||
                        helloChoice.Trim().Equals("y", StringComparison.OrdinalIgnoreCase);

        if (!useHello) goto skipHello;

        Console.WriteLine("Verifying identity with Windows Hello…");
        bool verified = await WindowsHelloService.VerifyAsync(
            ConsoleWindowHelper.GetHandle(),
            "MyKeePass wants to open your password database.");

        if (!verified)
        {
            Console.Error.WriteLine("  ✗ Windows Hello verification failed or was cancelled. Exiting.");
            await dbStream.DisposeAsync();
            driveService.Dispose();
            return;
        }

        // Retrieve as bytes — the Windows API string stays local inside the method
        // and becomes GC-eligible immediately.  We pin our byte array so the GC
        // cannot move it (preventing stale copies at old heap addresses).
        byte[]? storedBytes = WindowsHelloService.RetrievePasswordAsBytes(account.Email, selectedDb.FileName);
        if (storedBytes is not null)
        {
            var pin = GCHandle.Alloc(storedBytes, GCHandleType.Pinned);
            try
            {
                ks = new KeePassService(dbStream, storedBytes, keyStream);
                Console.WriteLine("  ✓ Database unlocked via Windows Hello.\n");
            }
            catch (InvalidOperationException)
            {
                // Stored password is stale — clear it and fall through to manual entry.
                Console.Error.WriteLine(
                    "  ✗ Stored password is incorrect (master password or key file may have changed).\n" +
                    "  Please enter the current master password.");
                WindowsHelloService.RemoveStoredPassword(account.Email, selectedDb.FileName);
                dbStream.Position = 0;
            }
            finally
            {
                Array.Clear(storedBytes, 0, storedBytes.Length);   // zero the bytes
                pin.Free();
            }
        }
    }

    skipHello:
    // ── 5b: Manual password entry (first run, or Hello path failed) ───────────
    if (ks is null)
    {
        const int maxTries = 3;
        bool      offered  = false;   // track whether we still need to offer Hello

        for (int attempt = 1; attempt <= maxTries; attempt++)
        {
            Console.Write($"Master password (attempt {attempt}/{maxTries}): ");

            // ReadPasswordAsBytes() never produces a string — chars are zero-filled
            // inside the method before it returns.
            byte[] pwdBytes = ConsoleHelper.ReadPasswordAsBytes();
            var    pin      = GCHandle.Alloc(pwdBytes, GCHandleType.Pinned);
            try
            {
                if (keyStream is not null) keyStream.Position = 0;
                ks = new KeePassService(dbStream, pwdBytes, keyStream);

                Console.WriteLine("  ✓ Database unlocked.\n");

                // Offer to save with Windows Hello for future logins.
                if (helloAvailable && !offered
                    && !WindowsHelloService.HasStoredPassword(account.Email, selectedDb.FileName))
                {
                    offered = true;
                    Console.Write("  Save password with Windows Hello for future logins? [y/N]: ");
                    string? answer = Console.ReadLine();
                    if (answer?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        // PasswordVault requires a string — unavoidable Windows API
                        // limitation.  The string is kept strictly local here.
                        WindowsHelloService.StorePassword(
                            account.Email,
                            selectedDb.FileName,
                            Encoding.UTF8.GetString(pwdBytes));
                        Console.WriteLine("  ✓ Password saved to Windows Credential Vault.\n");
                    }
                }

                break;  // success — exit the retry loop
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"  ✗ {ex.Message}");

                if (attempt == maxTries)
                {
                    Console.Error.WriteLine("Too many failed attempts. Exiting.");
                    await dbStream.DisposeAsync();
                    driveService.Dispose();
                    return;
                }

                // Exponential backoff: 1 s, 2 s, 4 s … slows down brute-force.
                int delaySec = (int)Math.Pow(2, attempt - 1);
                Console.Error.WriteLine($"  Waiting {delaySec}s before next attempt…");
                await Task.Delay(TimeSpan.FromSeconds(delaySec));

                dbStream.Position = 0;
            }
            finally
            {
                // Zero the bytes and release the pin regardless of success or failure.
                Array.Clear(pwdBytes, 0, pwdBytes.Length);
                pin.Free();
            }
        }
    }

    keepass = ks!;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: Unexpected failure opening database — {ex.Message}");
    await dbStream.DisposeAsync();
    driveService.Dispose();
    return;
}

// ─── Step 6: Run the interactive UI ───────────────────────────────────────────
IUserInterface ui = new TerminalGuiUI();   // swap to new ConsoleUI() to revert to plain console
await ui.RunAsync(keepass, driveService, fileId, selectedDb.FileName);

// ─── Cleanup ──────────────────────────────────────────────────────────────────
keepass.Dispose();
await dbStream.DisposeAsync();
keyStream?.Dispose();
driveService.Dispose();
ClipboardHelper.ClearClipboard(); // wipe any sensitive data still in clipboard

Console.Clear();
Console.WriteLine("Goodbye!");

// ═════════════════════════════════════════════════════════════════════════════
// Local helper functions
// ═════════════════════════════════════════════════════════════════════════════

static async Task<AppConfig> LoadOrCreateConfigAsync()
{
    const string configFile = "appsettings.json";
    var jsonOpts = new JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true };

    if (File.Exists(configFile))
    {
        try
        {
            string     json = await File.ReadAllTextAsync(configFile);
            AppConfig? cfg  = JsonSerializer.Deserialize<AppConfig>(json, jsonOpts);

            if (cfg is not null)
            {
                if (cfg.IsLegacyFormat)
                {
                    Console.WriteLine("Migrating legacy config to multi-account format…");
                    cfg = MigrateFromLegacy(cfg);
                    try
                    {
                        await File.WriteAllTextAsync(configFile,
                            JsonSerializer.Serialize(cfg, jsonOpts));
                        Console.WriteLine("  ✓ Config migrated and saved.\n");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  Warning: could not save migrated config — {ex.Message}\n");
                    }
                    return cfg;
                }

                if (cfg.IsValid)
                    return cfg;
            }
        }
        catch (JsonException ex)
        {
            Console.WriteLine(
                $"Warning: could not parse '{configFile}' ({ex.Message}). " +
                "Prompting instead.");
        }
    }

    // First-time setup
    Console.WriteLine($"'{configFile}' not found or incomplete. Please supply values:\n");
    string accountName = ConsoleHelper.Prompt("Account name",                                        "Personal");
    string email       = ConsoleHelper.Prompt("Google account email",                               "user@gmail.com");
    string dbFile      = ConsoleHelper.Prompt("KeePass database path (e.g. Keepass/passwords.kdbx)", "passwords.kdbx");

    var newCfg = new AppConfig
    {
        Accounts =
        [
            new AccountConfig
            {
                Name      = accountName,
                Email     = email,
                Databases = [ new DatabaseConfig { File = dbFile } ]
            }
        ]
    };

    try
    {
        await File.WriteAllTextAsync(configFile, JsonSerializer.Serialize(newCfg, jsonOpts));
        Console.WriteLine($"  Config saved to '{configFile}'.\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Warning: could not save config — {ex.Message}\n");
    }

    return newCfg;
}

static AppConfig MigrateFromLegacy(AppConfig legacy) => new AppConfig
{
    Accounts =
    [
        new AccountConfig
        {
            Name  = "Default",
            Email = "user",   // critical: preserves the old OAuth token cache key → no re-auth
            Databases =
            [
                new DatabaseConfig
                {
                    File = string.IsNullOrEmpty(legacy.DatabasePath)
                        ? legacy.DatabaseName!
                        : $"{legacy.DatabasePath}/{legacy.DatabaseName}"
                }
            ]
        }
    ]
};

static (AccountConfig Account, DatabaseConfig Database)? PickDatabase(AppConfig config)
{
    // Count total databases across all accounts
    int total = config.Accounts!.Sum(a => a.Databases.Count);

    // Skip the picker when there is exactly one database
    if (total == 1)
    {
        var onlyAccount = config.Accounts[0];
        var onlyDb      = onlyAccount.Databases[0];
        Console.WriteLine($"Config  →  [{onlyAccount.Name}]  {onlyDb.File}" +
            (onlyDb.KeyFile is not null ? "  [+key file]" : string.Empty));
        Console.WriteLine();
        return (onlyAccount, onlyDb);
    }

    // Build a flat numbered list for the menu
    var entries = new List<(AccountConfig Account, DatabaseConfig Database)>();
    foreach (var a in config.Accounts)
        foreach (var db in a.Databases)
            entries.Add((a, db));

    while (true)
    {
        Console.WriteLine("──────────────────────────────────────");
        Console.WriteLine("  Select a database to open:");
        Console.WriteLine("──────────────────────────────────────");
        Console.WriteLine();

        int idx = 1;
        foreach (var a in config.Accounts)
        {
            Console.WriteLine($"  [{a.Name}]  {a.Email}");
            foreach (var db in a.Databases)
            {
                string keyLabel = db.KeyFile is not null ? "  [+key file]" : string.Empty;
                Console.WriteLine($"    {idx}. {db.File}{keyLabel}");
                idx++;
            }
            Console.WriteLine();
        }

        Console.WriteLine("  0. Exit");
        Console.WriteLine("──────────────────────────────────────");
        Console.Write("Choice: ");

        string? input = Console.ReadLine();
        if (int.TryParse(input?.Trim(), out int choice))
        {
            if (choice == 0) return null;
            if (choice >= 1 && choice <= entries.Count)
                return entries[choice - 1];
        }

        Console.Error.WriteLine($"  Invalid choice. Enter 1–{entries.Count} or 0 to exit.\n");
    }
}
