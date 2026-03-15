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
using KeePassLib;
using mykeepass;
using mykeepass.Helpers;
using mykeepass.Models;
using mykeepass.Parsing;
using mykeepass.Services;

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

// ─── Step 4: Unlock the database (up to 3 password attempts) ──────────────────
KeePassService keepass;
try
{
    const int maxTries = 3;
    KeePassService? ks = null;

    for (int attempt = 1; attempt <= maxTries; attempt++)
    {
        Console.Write($"Master password (attempt {attempt}/{maxTries}): ");
        string pwd = ConsoleHelper.ReadPassword();

        try
        {
            ks = new KeePassService(dbStream, pwd);
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

    keepass = ks!;
    Console.WriteLine("  ✓ Database unlocked.\n");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: Unexpected failure opening database — {ex.Message}");
    await dbStream.DisposeAsync();
    driveService.Dispose();
    return;
}

// ─── Step 5: Interactive menu loop ────────────────────────────────────────────
await RunInteractiveLoopAsync(keepass, driveService, fileId, config.DatabaseName);

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

/// <summary>
/// Navigable interactive loop with two states:
///   • Folder view  — browse groups, run scoped operations
///   • Entry view   — inspect / edit a single entry's fields
/// </summary>
static async Task RunInteractiveLoopAsync(
    KeePassService     keepass,
    GoogleDriveService drive,
    string             fileId,
    string             fileName)
{
    var executor = new CommandExecutor(keepass, drive, fileId, fileName);

    string GroupPath(PwGroup group)
    {
        var parts = new List<string>();
        PwGroup? g = group;
        while (g is not null && g.ParentGroup is not null)
        {
            parts.Add(g.Name);
            g = g.ParentGroup;
        }
        if (parts.Count == 0) return "";
        parts.Reverse();
        return string.Join(" > ", parts.Select(p => $"▷ {p}"));
    }

    string Breadcrumb()
    {
        string groupCrumb = GroupPath(executor.CurrentGroup);
        if (string.IsNullOrEmpty(groupCrumb))
            groupCrumb = $"▷ {executor.CurrentGroup.Name}";
        if (executor.ViewedEntry is null) return groupCrumb;
        string title = executor.ViewedEntry.Strings.ReadSafe(PwDefs.TitleField);
        return $"{groupCrumb} > ◇ {title}";
    }

    void PrintMenu()
    {
        Console.WriteLine();
        Console.WriteLine("──────────────────────────────────────────────");
        Console.WriteLine($"  {Breadcrumb()}");

        if (executor.ViewedEntry is not null)
        {
            CommandExecutor.ShowEntry(keepass, executor.ViewedEntry);
            Console.WriteLine("──────────────────────────────────────────────");
            Console.WriteLine("  set <field> to <val>                        Set a field value (masked)");
            Console.WriteLine("  modify <field> to <val>                     Alias for set");
            Console.WriteLine("  add/update [key] <field> with value <val>   Set a field value (masked)");
            Console.WriteLine("  add/update [key] <field> = <val>            Shorthand (masked)");
            Console.WriteLine("  delete <field>             Remove a custom field");
            Console.WriteLine("  rename to <new-title>      Rename this entry");
            Console.WriteLine("  move to <folder>           Move this entry to another folder");
            Console.WriteLine("  copy <field>               Copy a field to clipboard (clears in 60 s)");
            Console.WriteLine("  update                     Edit all fields interactively");
            Console.WriteLine("  delete / remove            Move entry to recycle bin");
            Console.WriteLine("  save                       Upload database to Google Drive now");
            Console.WriteLine("  back                       Return to folder");
            Console.WriteLine("  exit                       Exit (offers upload if modified)");
        }
        else
        {
            Console.WriteLine("──────────────────────────────────────────────");
            Console.WriteLine("  add / create <name>              Create a new entry");
            Console.WriteLine("  add / create folder <name>       Create a subfolder");
            Console.WriteLine("  update / modify <name>           Edit an entry");
            Console.WriteLine("  delete / remove <name>           Move entry to recycle bin");
            Console.WriteLine("  delete / remove folder <name>    Move subfolder to recycle bin");
            Console.WriteLine("  rename <name> to <new>           Rename an entry or folder");
            Console.WriteLine("  rename folder <name> to <new>    Rename a folder explicitly");
            Console.WriteLine("  rename to <new>                  Rename the current folder");
            Console.WriteLine("  move <name> to <folder>          Move an entry or folder");
            Console.WriteLine("  move folder <name> to <folder>   Move a subfolder explicitly");
            Console.WriteLine("  search <term>                    Search entries");
            Console.WriteLine("  copy <field> from <name>         Copy field to clipboard (clears in 60 s)");
            Console.WriteLine("  list                             List contents of this folder");
            Console.WriteLine("  save                             Upload database to Google Drive now");
            Console.WriteLine("  back                             Go to parent folder");
            Console.WriteLine("  exit                             Exit (offers upload if modified)");
            Console.WriteLine();
            Console.WriteLine("  select <name>               Open folder or entry (auto-detect)");
            Console.WriteLine("  select folder <name>        Navigate into a subfolder");
            Console.WriteLine("  select entry  <name>        View an entry");
        }

        Console.WriteLine("──────────────────────────────────────────────");
        Console.Write("> ");
    }

    // History of commands entered this session (oldest → newest).
    var history = new List<string>();

    // Returns the buffer index where the secret value starts (masking begins),
    // or -1 if the current input does not have a maskable value yet.
    //
    // Handled forms:
    //   add/create/insert/update [key] <field> with value <val>
    //   add/create/insert/update [key] <field> = <val>
    //   set   [key] <field> to <val>
    //   modify [key] <field> to <val>
    static int ValueStartIndex(string buf)
    {
        string lo = buf.ToLowerInvariant();

        if (lo.StartsWith("add ")    || lo.StartsWith("create ") ||
            lo.StartsWith("insert ") || lo.StartsWith("update "))
        {
            // "with value" form: mask starts after the trailing space of "with value "
            int wvIdx = lo.IndexOf(" with value ");
            if (wvIdx >= 0)
            {
                int start = wvIdx + " with value ".Length;
                if (buf.Length <= start) return -1;
                if (lo[start..].StartsWith("from ")) return -1;
                return start;
            }

            // "= val" form: mask starts after " = " (spaces required around =)
            int eqIdx = lo.IndexOf(" = ");
            if (eqIdx >= 0)
            {
                int start = eqIdx + " = ".Length;
                return buf.Length > start ? start : -1;
            }
        }

        // "set/modify [key] <field> to <val>" — mask starts after " to "
        if (lo.StartsWith("set ") || lo.StartsWith("modify "))
        {
            int idx = lo.IndexOf(" to ");
            if (idx >= 0)
            {
                int start = idx + " to ".Length;
                if (buf.Length <= start) return -1;
                if (lo[start..].StartsWith("value from ")) return -1;
                return start;
            }
        }

        return -1;
    }

    // Reads a full line preserving original case.
    // • Ctrl+L  — clear screen and redraw menu
    // • ↑ / ↓   — navigate command history (shell-style)
    // • ← / →   — move cursor within the line
    // • add/insert/update … with value <val> — masks the value as it is typed
    string ReadChoice()
    {
        var    sb         = new System.Text.StringBuilder();
        int    cursorPos  = 0;             // screen column of the cursor within the input
        int    histPos    = history.Count; // past-the-end = "live" input slot
        string savedInput = "";            // live input saved while navigating up

        // Render sb[from..] with masking applied (vs = ValueStartIndex result, -1 = none).
        // Leaves the console cursor at the right edge (column sb.Length).
        void RenderTail(int from, int vs)
        {
            if (from >= sb.Length) return;
            if (vs < 0)
                Console.Write(sb.ToString()[from..]);
            else if (from >= vs)
                Console.Write(new string('*', sb.Length - from));
            else
            {
                Console.Write(sb.ToString()[from..vs]);
                Console.Write(new string('*', sb.Length - vs));
            }
        }

        // Erase current line and write newText, masking the value part if present.
        void ReplaceText(string newText)
        {
            // Move to column 0, then erase old content.
            if (cursorPos > 0) Console.Write(new string('\b', cursorPos));
            if (sb.Length > 0)
            {
                Console.Write(new string(' ',  sb.Length));
                Console.Write(new string('\b', sb.Length));
            }
            sb.Clear();
            sb.Append(newText);
            cursorPos = newText.Length;   // cursor at end after replace

            int vs = ValueStartIndex(newText);
            if (vs >= 0)
            {
                Console.Write(newText[..vs]);                        // plain prefix
                Console.Write(new string('*', newText.Length - vs)); // masked value
            }
            else
            {
                Console.Write(newText);
            }
        }

        while (true)
        {
            var k = Console.ReadKey(intercept: true);

            if (k.Modifiers.HasFlag(ConsoleModifiers.Control) && k.Key == ConsoleKey.L)
            {
                Console.Clear();
                PrintMenu();
                sb.Clear();
                cursorPos  = 0;
                histPos    = history.Count;
                savedInput = "";
                continue;
            }

            if (k.Key == ConsoleKey.UpArrow)
            {
                if (history.Count == 0) continue;
                if (histPos == history.Count)
                    savedInput = sb.ToString(); // preserve live input before going up
                if (histPos > 0)
                    ReplaceText(history[--histPos]);
                continue;
            }

            if (k.Key == ConsoleKey.DownArrow)
            {
                if (histPos >= history.Count) continue;
                histPos++;
                ReplaceText(histPos == history.Count ? savedInput : history[histPos]);
                continue;
            }

            if (k.Key == ConsoleKey.LeftArrow)
            {
                if (cursorPos > 0) { Console.Write('\b'); cursorPos--; }
                continue;
            }

            if (k.Key == ConsoleKey.RightArrow)
            {
                if (cursorPos < sb.Length)
                {
                    int vs = ValueStartIndex(sb.ToString());
                    Console.Write(vs >= 0 && cursorPos >= vs ? '*' : sb[cursorPos]);
                    cursorPos++;
                }
                continue;
            }

            if (k.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                string result = sb.ToString().Trim();
                // Add to history, skipping blank lines and consecutive duplicates.
                if (!string.IsNullOrEmpty(result)
                    && (history.Count == 0 || history[^1] != result))
                    history.Add(result);
                histPos    = history.Count;
                savedInput = "";
                cursorPos  = 0;
                return result;
            }

            if (k.Key == ConsoleKey.Backspace)
            {
                if (cursorPos > 0)
                {
                    int vsBeforeBs   = ValueStartIndex(sb.ToString());
                    int oldCursorPos = cursorPos;
                    sb.Remove(cursorPos - 1, 1);
                    cursorPos--;
                    int vsAfterBs = ValueStartIndex(sb.ToString());

                    if ((vsBeforeBs >= 0) == (vsAfterBs >= 0))
                    {
                        // State unchanged — step back, re-render tail, erase last old char.
                        Console.Write('\b');
                        RenderTail(cursorPos, vsAfterBs);
                        Console.Write(' ');   // erase the extra character at old end
                        Console.Write(new string('\b', sb.Length - cursorPos + 1));
                    }
                    else
                    {
                        // State changed — full redraw.
                        Console.Write(new string('\b', oldCursorPos));
                        Console.Write(new string(' ',  sb.Length + 1));
                        Console.Write(new string('\b', sb.Length + 1));
                        string full = sb.ToString();
                        if (vsAfterBs >= 0) { Console.Write(full[..vsAfterBs]); Console.Write(new string('*', full.Length - vsAfterBs)); }
                        else                { Console.Write(full); }
                        int moveBack = sb.Length - cursorPos;
                        if (moveBack > 0) Console.Write(new string('\b', moveBack));
                    }
                }
                continue;
            }

            if (k.KeyChar != '\0')
            {
                int vsBefore     = ValueStartIndex(sb.ToString());
                int oldCursorPos = cursorPos;
                sb.Insert(cursorPos, k.KeyChar);
                cursorPos++;
                int vsAfter = ValueStartIndex(sb.ToString());

                if (vsBefore < 0 && vsAfter < 0)
                {
                    // All plain — write char then re-render suffix.
                    Console.Write(k.KeyChar);
                    RenderTail(cursorPos, -1);
                    int moveBack = sb.Length - cursorPos;
                    if (moveBack > 0) Console.Write(new string('\b', moveBack));
                }
                else if (vsBefore >= 0 && vsAfter >= 0)
                {
                    // Still masking — write '*' then re-render suffix as '*'.
                    Console.Write('*');
                    int tailLen = sb.Length - cursorPos;
                    if (tailLen > 0)
                    {
                        Console.Write(new string('*', tailLen));
                        Console.Write(new string('\b', tailLen));
                    }
                }
                else
                {
                    // Masking state flipped — full redraw.
                    Console.Write(new string('\b', oldCursorPos));
                    Console.Write(new string(' ',  sb.Length));
                    Console.Write(new string('\b', sb.Length));
                    string full = sb.ToString();
                    if (vsAfter >= 0) { Console.Write(full[..vsAfter]); Console.Write(new string('*', full.Length - vsAfter)); }
                    else              { Console.Write(full); }
                    int moveBack = sb.Length - cursorPos;
                    if (moveBack > 0) Console.Write(new string('\b', moveBack));
                }
            }
        }
    }

    // ── Main loop ─────────────────────────────────────────────────────────────
    while (true)
    {
        PrintMenu();
        ICommand command = CommandParser.Parse(ReadChoice());
        if (!await executor.ExecuteAsync(command)) return;
    }
}
