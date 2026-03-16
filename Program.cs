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

        string? storedPwd = WindowsHelloService.RetrievePassword(config.DatabaseName);
        if (storedPwd is not null)
        {
            try
            {
                ks = new KeePassService(dbStream, storedPwd);
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
        }
    }

    // ── 4b: Manual password entry (first run, or Hello path failed) ───────────
    if (ks is null)
    {
        const int maxTries  = 3;
        string?   successPwd = null;

        for (int attempt = 1; attempt <= maxTries; attempt++)
        {
            Console.Write($"Master password (attempt {attempt}/{maxTries}): ");
            string pwd = ConsoleHelper.ReadPassword();

            try
            {
                ks         = new KeePassService(dbStream, pwd);
                successPwd = pwd;
                break;
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

                dbStream.Position = 0;
            }
        }

        Console.WriteLine("  ✓ Database unlocked.\n");

        // Offer to save with Windows Hello for next time.
        if (helloAvailable && successPwd is not null
            && !WindowsHelloService.HasStoredPassword(config.DatabaseName))
        {
            Console.Write("  Save password with Windows Hello for future logins? [y/N]: ");
            string? answer = Console.ReadLine();
            if (answer?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true)
            {
                WindowsHelloService.StorePassword(config.DatabaseName, successPwd);
                Console.WriteLine("  ✓ Password saved to Windows Credential Vault.\n");
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
