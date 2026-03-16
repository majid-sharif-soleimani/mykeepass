/*
 * MyKeePass — Google Drive × KeePass 2 Console Manager
 * ══════════════════════════════════════════════════════
 *
 * SETUP (one-time):
 *   1. Open Google Cloud Console → APIs & Services → Enable "Google Drive API".
 *   2. Create an OAuth 2.0 credential (Application type: Desktop app).
 *   3. Download the JSON → save it as credentials.json next to this executable.
 *      ⚠  NEVER commit credentials.json to source control — add it to .gitignore.
 *   4. Optionally edit appsettings.json to pre-fill DatabaseName and DatabasePath.
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

// ─── Step 2: Authenticate with Google Drive ───────────────────────────────────
Console.WriteLine("Authenticating with Google Drive…");
GoogleDriveService driveService;
try
{
    driveService = await GoogleDriveService.CreateAsync("credentials.json");
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

// ─── Step 3: Find and download the .kdbx file ─────────────────────────────────
Console.WriteLine($"Searching Drive for '{config.DatabaseName}'…");
MemoryStream dbStream;
string       fileId;
try
{
    var (stream, id) = await driveService.DownloadFileAsync(
        config.DatabaseName, config.DatabasePath);

    if (stream is null || id is null)
    {
        Console.Error.WriteLine(
            $"ERROR: '{config.DatabaseName}' was not found on Google Drive.\n" +
            "Check the filename and folder path in appsettings.json.");
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

// ─── Step 4: Unlock the database ──────────────────────────────────────────────
KeePassService keepass;
try
{
    bool helloAvailable = await WindowsHelloService.IsAvailableAsync();
    KeePassService? ks  = null;

    // ── 4a: Windows Hello path ────────────────────────────────────────────────
    if (helloAvailable && WindowsHelloService.HasStoredPassword(config.DatabaseName))
    {
        Console.WriteLine("Verifying identity with Windows Hello…");
        bool verified = await WindowsHelloService.VerifyAsync(
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
        byte[]? storedBytes = WindowsHelloService.RetrievePasswordAsBytes(config.DatabaseName);
        if (storedBytes is not null)
        {
            var pin = GCHandle.Alloc(storedBytes, GCHandleType.Pinned);
            try
            {
                ks = new KeePassService(dbStream, storedBytes);
                Console.WriteLine("  ✓ Database unlocked via Windows Hello.\n");
            }
            catch (InvalidOperationException)
            {
                // Stored password is stale — clear it and fall through to manual entry.
                Console.Error.WriteLine(
                    "  ✗ Stored password is incorrect (master password may have changed).\n" +
                    "  Please enter the current master password.");
                WindowsHelloService.RemoveStoredPassword(config.DatabaseName);
                dbStream.Position = 0;
            }
            finally
            {
                Array.Clear(storedBytes, 0, storedBytes.Length);   // zero the bytes
                pin.Free();
            }
        }
    }

    // ── 4b: Manual password entry (first run, or Hello path failed) ───────────
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
                ks = new KeePassService(dbStream, pwdBytes);

                Console.WriteLine("  ✓ Database unlocked.\n");

                // Offer to save with Windows Hello for future logins.
                if (helloAvailable && !offered
                    && !WindowsHelloService.HasStoredPassword(config.DatabaseName))
                {
                    offered = true;
                    Console.Write("  Save password with Windows Hello for future logins? [y/N]: ");
                    string? answer = Console.ReadLine();
                    if (answer?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        // PasswordVault requires a string — unavoidable Windows API
                        // limitation.  The string is kept strictly local here.
                        WindowsHelloService.StorePassword(
                            config.DatabaseName,
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

// ─── Step 5: Run the interactive UI ───────────────────────────────────────────
IUserInterface ui = new TerminalGuiUI();   // swap to new ConsoleUI() to revert to plain console
await ui.RunAsync(keepass, driveService, fileId, config.DatabaseName);

// ─── Cleanup ──────────────────────────────────────────────────────────────────
keepass.Dispose();
await dbStream.DisposeAsync();
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

    if (File.Exists(configFile))
    {
        try
        {
            string    json = await File.ReadAllTextAsync(configFile);
            AppConfig? cfg = JsonSerializer.Deserialize<AppConfig>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (cfg is not null && !string.IsNullOrWhiteSpace(cfg.DatabaseName))
            {
                Console.WriteLine($"Config  →  file: '{cfg.DatabaseName}'  " +
                    $"path: '{(string.IsNullOrWhiteSpace(cfg.DatabasePath) ? "(root)" : cfg.DatabasePath)}'");
                Console.WriteLine();
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

    Console.WriteLine($"'{configFile}' not found or incomplete. Please supply values:\n");
    string name = ConsoleHelper.Prompt("KeePass database filename", "passwords.kdbx");
    string path = ConsoleHelper.Prompt("Google Drive folder path  (empty = root)", "");

    var newCfg = new AppConfig { DatabaseName = name, DatabasePath = path };

    try
    {
        string json = JsonSerializer.Serialize(
            newCfg, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(configFile, json);
        Console.WriteLine($"  Config saved to '{configFile}'.\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Warning: could not save config — {ex.Message}\n");
    }

    return newCfg;
}
