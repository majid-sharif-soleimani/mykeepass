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
using mykeepass.Helpers;
using mykeepass.Models;
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
    PwGroup  currentGroup = keepass.RootGroup;
    PwEntry? viewedEntry  = null;

    static string GroupPath(PwGroup group)
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
        string groupCrumb = GroupPath(currentGroup);
        if (string.IsNullOrEmpty(groupCrumb))
            groupCrumb = $"▷ {currentGroup.Name}";
        if (viewedEntry is null) return groupCrumb;
        string title = viewedEntry.Strings.ReadSafe(PwDefs.TitleField);
        return $"{groupCrumb} > ◇ {title}";
    }

    void PrintMenu()
    {
        Console.WriteLine();
        Console.WriteLine("──────────────────────────────────────────────");
        Console.WriteLine($"  {Breadcrumb()}");

        if (viewedEntry is not null)
        {
            ShowEntry(keepass, viewedEntry);
            Console.WriteLine("──────────────────────────────────────────────");
            Console.WriteLine("  add <key> with value <val>  Add or update a field");
            Console.WriteLine("  copy <field>                Copy a field to clipboard (clears in 60 s)");
            Console.WriteLine("  update                      Edit fields interactively");
            Console.WriteLine("  delete                      Move to recycle bin");
            Console.WriteLine("  back                        Return to folder");
            Console.WriteLine("  exit                        Exit");
        }
        else
        {
            Console.WriteLine("──────────────────────────────────────────────");
            Console.WriteLine("  add <name>                  Create a new entry");
            Console.WriteLine("  add folder <name>           Create a subfolder");
            Console.WriteLine("  update <name>               Edit an entry");
            Console.WriteLine("  delete <name>               Move entry to recycle bin");
            Console.WriteLine("  search <term>               Search entries");
            Console.WriteLine("  copy <field> from <name>    Copy field to clipboard (clears in 60 s)");
            Console.WriteLine("  list                        List contents of this folder");
            Console.WriteLine("  back                        Go to parent folder");
            Console.WriteLine("  exit                        Exit");
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

    // Reads a full line preserving original case.
    // • Ctrl+L  — clear screen and redraw menu
    // • ↑ / ↓   — navigate command history (shell-style)
    string ReadChoice()
    {
        var    sb         = new System.Text.StringBuilder();
        int    histPos    = history.Count; // past-the-end = "live" input slot
        string savedInput = "";            // live input saved while navigating up

        // Erase whatever is on the current input line and write newText instead.
        void ReplaceText(string newText)
        {
            int len = sb.Length;
            if (len > 0)
            {
                Console.Write(new string('\b', len));
                Console.Write(new string(' ',  len));
                Console.Write(new string('\b', len));
            }
            sb.Clear();
            sb.Append(newText);
            Console.Write(newText);
        }

        while (true)
        {
            var k = Console.ReadKey(intercept: true);

            if (k.Modifiers.HasFlag(ConsoleModifiers.Control) && k.Key == ConsoleKey.L)
            {
                Console.Clear();
                PrintMenu();
                sb.Clear();
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
                return result;
            }

            if (k.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) { sb.Remove(sb.Length - 1, 1); Console.Write("\b \b"); }
                continue;
            }

            if (k.KeyChar != '\0') { sb.Append(k.KeyChar); Console.Write(k.KeyChar); }
        }
    }

    // ── Main loop ─────────────────────────────────────────────────────────────
    while (true)
    {
        PrintMenu();
        string cmd      = ReadChoice();
        string cmdLower = cmd.ToLowerInvariant(); // for command matching (case-insensitive)

        // ── Global navigation prefix commands ─────────────────────────────────

        // "select <name>" — auto-detect folder vs entry; disambiguate if both match.
        if (cmdLower.StartsWith("select ")
            && !cmdLower.StartsWith("select folder ")
            && !cmdLower.StartsWith("select entry "))
        {
            if (viewedEntry is not null) { Console.WriteLine("  Type 'back' to exit the entry view first."); continue; }
            string name = cmd["select ".Length..].Trim();
            if (string.IsNullOrEmpty(name)) { Console.WriteLine("  Specify a name."); continue; }

            var matchFolder = keepass.FindChildGroup(currentGroup, name);
            var matchEntry  = keepass.FindEntryInGroup(currentGroup, name);

            if (matchFolder is null && matchEntry is null)
                Console.WriteLine($"  Nothing matching '{name}' found in this folder.");
            else if (matchFolder is not null && matchEntry is not null)
            {
                Console.WriteLine($"  Ambiguous — both a folder and an entry match '{name}':");
                Console.WriteLine($"    folder : {matchFolder.Name}");
                Console.WriteLine($"    entry  : {matchEntry.Strings.ReadSafe(PwDefs.TitleField)}");
                Console.WriteLine($"  Use 'select folder {name}' or 'select entry {name}' to disambiguate.");
            }
            else if (matchFolder is not null)
            {
                currentGroup = matchFolder;
                Console.WriteLine($"  Entered '{matchFolder.Name}'.");
            }
            else
            {
                viewedEntry = matchEntry;
            }
            continue;
        }

        if (cmdLower.StartsWith("select folder "))
        {
            if (viewedEntry is not null) { Console.WriteLine("  Type 'back' to exit the entry view first."); continue; }
            string prefix = cmd["select folder ".Length..].Trim();
            if (string.IsNullOrEmpty(prefix)) { Console.WriteLine("  Please specify a folder name."); continue; }
            var target = keepass.FindChildGroup(currentGroup, prefix);
            if (target is null) Console.WriteLine($"  No subfolder matching '{prefix}' found.");
            else { currentGroup = target; Console.WriteLine($"  Entered '{target.Name}'."); }
            continue;
        }

        if (cmdLower.StartsWith("select entry "))
        {
            if (viewedEntry is not null) { Console.WriteLine("  Type 'back' to exit the current entry first."); continue; }
            string prefix = cmd["select entry ".Length..].Trim();
            if (string.IsNullOrEmpty(prefix)) { Console.WriteLine("  Please specify an entry name."); continue; }
            var target = keepass.FindEntryInGroup(currentGroup, prefix);
            if (target is null) Console.WriteLine($"  No entry matching '{prefix}' found.");
            else viewedEntry = target;
            continue;
        }

        // ── Entry view ────────────────────────────────────────────────────────
        if (viewedEntry is not null)
        {
            // "add/insert <key> with value <value>"  — key and value preserve original case
            if ((cmdLower.StartsWith("add ") || cmdLower.StartsWith("insert "))
                && cmdLower.Contains(" with value "))
            {
                int    prefixLen     = cmdLower.StartsWith("add ") ? 4 : 7; // "add " or "insert "
                int    withValueIdx  = cmdLower.IndexOf(" with value ");
                string key           = cmd[prefixLen..withValueIdx].Trim();
                string value         = cmd[(withValueIdx + " with value ".Length)..].Trim();

                if (string.IsNullOrEmpty(key))
                    Console.WriteLine("  Key cannot be empty.  e.g.: add password with value S3cr3t");
                else
                {
                    keepass.SetEntryField(viewedEntry, key, value);
                    Console.WriteLine($"  ✓ Field '{key}' set.");
                    if (keepass.IsModified) await OfferUploadAsync(keepass, drive, fileId, fileName);
                }
                continue;
            }

            // "copy <field>" — named copy within entry view
            if (cmdLower.StartsWith("copy "))
            {
                string fieldKey = cmd["copy ".Length..].Trim();
                if (!string.IsNullOrEmpty(fieldKey))
                {
                    CopyNamedFieldFromEntry(keepass, viewedEntry, fieldKey);
                    continue;
                }
                // bare "copy " (just spaces) — fall through to switch's "copy" case
            }

            switch (cmdLower)
            {
                case "copy":
                    CopyAttributeFromEntry(keepass, viewedEntry);
                    break;

                case "update": case "edit":
                    EditViewedEntry(keepass, viewedEntry);
                    if (keepass.IsModified) await OfferUploadAsync(keepass, drive, fileId, fileName);
                    break;

                case "delete": case "del": case "rm":
                    if (ConfirmDeleteEntry(keepass, viewedEntry))
                    {
                        viewedEntry = null;
                        if (keepass.IsModified) await OfferUploadAsync(keepass, drive, fileId, fileName);
                    }
                    break;

                case "back":
                    viewedEntry = null;
                    break;

                case "exit": case "quit": case "q":
                    return;

                default:
                    Console.WriteLine("  Unknown command.");
                    break;
            }
            continue;
        }

        // ── Folder view prefix commands ───────────────────────────────────────

        // "add folder [<name>]" / "folder [<name>]"
        if (cmdLower == "add folder" || cmdLower == "folder" || cmdLower.StartsWith("add folder "))
        {
            string name = cmdLower.StartsWith("add folder ")
                ? cmd["add folder ".Length..].Trim()
                : ConsoleHelper.Prompt("\nFolder name").Trim();
            if (string.IsNullOrEmpty(name)) { Console.WriteLine("  Folder name cannot be empty."); continue; }
            if (keepass.CreateGroup(currentGroup, name))
                Console.WriteLine($"  ✓ Folder '{name}' created.");
            else
                Console.WriteLine($"  A folder named '{name}' already exists here.");
            if (keepass.IsModified) await OfferUploadAsync(keepass, drive, fileId, fileName);
            continue;
        }

        // "add entry <name>" / "new entry <name>" → quick-create entry, navigate to it
        if (cmdLower.StartsWith("add entry ") || cmdLower.StartsWith("new entry "))
        {
            string name = cmdLower.StartsWith("add entry ")
                ? cmd["add entry ".Length..].Trim()
                : cmd["new entry ".Length..].Trim();
            if (string.IsNullOrEmpty(name)) { Console.WriteLine("  Entry name cannot be empty."); continue; }
            viewedEntry = keepass.AddQuickEntry(currentGroup, name);
            Console.WriteLine($"  ✓ Entry '{name}' created. Use 'add <key> with value <val>' to fill in fields.");
            if (keepass.IsModified) await OfferUploadAsync(keepass, drive, fileId, fileName);
            continue;
        }

        // "add <name>" / "new <name>" — shorthand quick-create (caught after "add folder" / "add entry")
        if (cmdLower.StartsWith("add ") || cmdLower.StartsWith("new "))
        {
            string name = cmdLower.StartsWith("add ")
                ? cmd["add ".Length..].Trim()
                : cmd["new ".Length..].Trim();
            if (string.IsNullOrEmpty(name)) { Console.WriteLine("  Entry name cannot be empty."); continue; }
            viewedEntry = keepass.AddQuickEntry(currentGroup, name);
            Console.WriteLine($"  ✓ Entry '{name}' created. Use 'add <key> with value <val>' to fill in fields.");
            if (keepass.IsModified) await OfferUploadAsync(keepass, drive, fileId, fileName);
            continue;
        }

        // "delete/del/rm/remove [<name>]"
        if (cmdLower == "delete" || cmdLower == "del" || cmdLower == "rm" || cmdLower == "remove" ||
            cmdLower.StartsWith("delete ") || cmdLower.StartsWith("del ") ||
            cmdLower.StartsWith("rm ")     || cmdLower.StartsWith("remove "))
        {
            string prefix = "";
            if      (cmdLower.StartsWith("delete ")) prefix = cmd["delete ".Length..].Trim();
            else if (cmdLower.StartsWith("del "))    prefix = cmd["del ".Length..].Trim();
            else if (cmdLower.StartsWith("rm "))     prefix = cmd["rm ".Length..].Trim();
            else if (cmdLower.StartsWith("remove ")) prefix = cmd["remove ".Length..].Trim();

            if (string.IsNullOrEmpty(prefix))
                DeleteEntry(keepass, currentGroup);
            else
                DeleteEntryByName(keepass, currentGroup, prefix);

            if (keepass.IsModified) await OfferUploadAsync(keepass, drive, fileId, fileName);
            continue;
        }

        // "search/find [<term>]"
        if (cmdLower == "search" || cmdLower == "find" ||
            cmdLower.StartsWith("search ") || cmdLower.StartsWith("find "))
        {
            string term = "";
            if      (cmdLower.StartsWith("search ")) term = cmd["search ".Length..].Trim();
            else if (cmdLower.StartsWith("find "))   term = cmd["find ".Length..].Trim();

            SearchEntries(keepass, currentGroup, term);
            continue;
        }

        // "update/edit [<name>]"
        if (cmdLower == "update" || cmdLower == "edit" ||
            cmdLower.StartsWith("update ") || cmdLower.StartsWith("edit "))
        {
            string prefix = "";
            if      (cmdLower.StartsWith("update ")) prefix = cmd["update ".Length..].Trim();
            else if (cmdLower.StartsWith("edit "))   prefix = cmd["edit ".Length..].Trim();

            if (string.IsNullOrEmpty(prefix))
            {
                EditEntry(keepass, currentGroup);
            }
            else
            {
                var entry = keepass.FindEntryInGroup(currentGroup, prefix);
                if (entry is null)
                    Console.WriteLine($"  No entry matching '{prefix}' found.");
                else
                {
                    viewedEntry = entry;
                    EditViewedEntry(keepass, entry);
                }
            }
            if (keepass.IsModified) await OfferUploadAsync(keepass, drive, fileId, fileName);
            continue;
        }

        // "copy <field> from <entry>"
        if (cmdLower.StartsWith("copy ") && cmdLower.Contains(" from "))
        {
            int    fromIdx   = cmdLower.LastIndexOf(" from ");
            string fieldKey  = cmd[5..fromIdx].Trim();         // after "copy ", preserve case
            string entryName = cmd[(fromIdx + 6)..].Trim();    // after " from ", preserve case

            if (string.IsNullOrEmpty(fieldKey))
                Console.WriteLine("  Specify a field name.  e.g.: copy password from gmail");
            else if (string.IsNullOrEmpty(entryName))
                Console.WriteLine("  Specify an entry name. e.g.: copy password from gmail");
            else
            {
                var entry = keepass.FindEntryInGroup(currentGroup, entryName);
                if (entry is null) Console.WriteLine($"  No entry matching '{entryName}' found.");
                else               CopyNamedFieldFromEntry(keepass, entry, fieldKey);
            }
            continue;
        }

        // "copy <field>" without "from" — hint
        if (cmdLower.StartsWith("copy "))
        {
            Console.WriteLine("  Usage: copy <field> from <entry>   e.g.: copy password from gmail");
            Console.WriteLine("  Or use bare 'copy' for interactive selection.");
            continue;
        }

        // ── Bare commands (no arguments) ──────────────────────────────────────
        switch (cmdLower)
        {
            case "add":
                AddEntry(keepass, currentGroup);
                if (keepass.IsModified) await OfferUploadAsync(keepass, drive, fileId, fileName);
                break;

            case "copy":
                CopyAttribute(keepass, currentGroup);
                break;

            case "list": case "ls":
                keepass.ListGroup(currentGroup);
                break;

            case "back":
                if (!ReferenceEquals(currentGroup, keepass.RootGroup))
                {
                    currentGroup = currentGroup.ParentGroup ?? keepass.RootGroup;
                    Console.WriteLine($"  Back to '{currentGroup.Name}'.");
                }
                else
                {
                    Console.WriteLine("  Already at root.");
                }
                break;

            case "exit": case "quit": case "q":
                return;

            default:
                Console.WriteLine("  Unknown command. Type 'list' to see folder contents.");
                break;
        }
    }
}

/// <summary>Offers to upload when the database has unsaved changes.</summary>
static async Task OfferUploadAsync(
    KeePassService     keepass,
    GoogleDriveService drive,
    string             fileId,
    string             fileName)
{
    Console.Write("\nUpload changes to Google Drive? (y/n): ");
    if (Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true)
        await UploadChangesAsync(keepass, drive, fileId, fileName);
}

/// <summary>Prints all fields of <paramref name="entry"/> to the console.</summary>
static void ShowEntry(KeePassService keepass, PwEntry entry)
{
    var fields = keepass.GetEntryFields(entry);
    Console.WriteLine();
    foreach (var (name, value, isProtected, isCustom) in fields)
    {
        string display = isProtected ? "●●●●●●●●" : value;
        string tag     = isCustom    ? " [custom]" : string.Empty;
        Console.WriteLine($"  {name,-16}: {display}{tag}");
    }
}

/// <summary>
/// Interactively creates a new entry (all fields prompted).
/// </summary>
static void AddEntry(KeePassService keepass, PwGroup targetGroup)
{
    Console.WriteLine();

    string title;
    do
    {
        title = ConsoleHelper.Prompt("Title").Trim();
        if (string.IsNullOrEmpty(title))
            Console.WriteLine("  Title cannot be empty.");
    }
    while (string.IsNullOrEmpty(title));

    string? username = NullIfEmpty(ConsoleHelper.Prompt("Username (Enter to skip)"));
    string? url      = NullIfEmpty(ConsoleHelper.Prompt("Website  (Enter to skip)"));
    string? notes    = NullIfEmpty(ConsoleHelper.Prompt("Notes    (Enter to skip)"));

    Console.Write("Password (Enter to skip): ");
    string? password = NullIfEmpty(ConsoleHelper.ReadPassword());

    var customFields = new List<(string Key, string Value)>();
    Console.WriteLine("\n  Custom fields (all values are protected). Leave key blank to finish.");
    while (true)
    {
        string key = ConsoleHelper.Prompt("  Key").Trim();
        if (string.IsNullOrEmpty(key)) break;

        Console.Write("  Value (hidden): ");
        string value = ConsoleHelper.ReadPassword();
        customFields.Add((key, value));
    }

    keepass.AddEntryTo(targetGroup, title, username, password, url, notes, customFields);
    Console.WriteLine($"\n  ✓ Entry '{title}' added to '{targetGroup.Name}'.");
}

/// <summary>
/// Copies a named field (case-insensitive key match) from <paramref name="entry"/>
/// to the clipboard using the secure clipboard helper.
/// </summary>
static void CopyNamedFieldFromEntry(KeePassService keepass, PwEntry entry, string fieldKey)
{
    var fields = keepass.GetEntryFields(entry);
    int idx = -1;
    for (int i = 0; i < fields.Count; i++)
        if (fields[i].Name.Equals(fieldKey, StringComparison.OrdinalIgnoreCase))
        { idx = i; break; }

    if (idx < 0)
    {
        Console.WriteLine($"  Field '{fieldKey}' not found.");
        if (fields.Count > 0)
            Console.WriteLine($"  Available: {string.Join(", ", fields.Select(f => f.Name))}");
        return;
    }

    ClipboardHelper.SetSecureText(fields[idx].Value);
    Console.WriteLine($"  ✓ '{fields[idx].Name}' copied. Clipboard clears in 60 s.");
}

/// <summary>
/// Interactive: shows fields of <paramref name="entry"/>, lets the user pick one,
/// and copies it to the clipboard.
/// </summary>
static void CopyAttributeFromEntry(KeePassService keepass, PwEntry entry)
{
    var fields = keepass.GetEntryFields(entry);
    if (fields.Count == 0) { Console.WriteLine("  Entry has no fields."); return; }

    Console.WriteLine($"\n  Fields in '{entry.Strings.ReadSafe(PwDefs.TitleField)}':");
    for (int i = 0; i < fields.Count; i++)
    {
        string display = fields[i].IsProtected ? "●●●●●●●●" : fields[i].Value;
        string tag     = fields[i].IsCustom    ? " [custom]" : string.Empty;
        Console.WriteLine($"    [{i + 1}] {fields[i].Name,-16}: {display}{tag}");
    }

    Console.Write($"\nField to copy (1–{fields.Count}): ");
    if (!int.TryParse(Console.ReadLine(), out int fieldNum)
        || fieldNum < 1 || fieldNum > fields.Count)
    {
        Console.WriteLine("  Invalid number — nothing copied.");
        return;
    }

    var chosen = fields[fieldNum - 1];
    ClipboardHelper.SetSecureText(chosen.Value);
    Console.WriteLine($"  ✓ '{chosen.Name}' copied. Clipboard clears in 60 s.");
}

/// <summary>
/// Interactive: lists entries in <paramref name="scope"/>, lets the user pick one,
/// then copy a field.
/// </summary>
static void CopyAttribute(KeePassService keepass, PwGroup scope)
{
    var entries = keepass.GetEntries(scope);
    if (entries.Count == 0) { Console.WriteLine("  No entries in this folder."); return; }

    keepass.ListGroup(scope);

    Console.Write($"\nEntry number to copy from (1–{entries.Count}): ");
    if (!int.TryParse(Console.ReadLine(), out int num) || num < 1 || num > entries.Count)
    {
        Console.WriteLine("  Invalid number — nothing copied.");
        return;
    }

    CopyAttributeFromEntry(keepass, entries[num - 1]);
}

/// <summary>
/// Searches entries within <paramref name="scope"/>.
/// If <paramref name="term"/> is empty the user is prompted for it.
/// </summary>
static void SearchEntries(KeePassService keepass, PwGroup scope, string term = "")
{
    if (string.IsNullOrEmpty(term))
        term = ConsoleHelper.Prompt("\nSearch").Trim();
    if (string.IsNullOrEmpty(term)) { Console.WriteLine("  Empty search term."); return; }

    var results = keepass.Search(term, scope);

    if (results.Count == 0) { Console.WriteLine($"  No entries matched '{term}'."); return; }

    Console.WriteLine($"\n  {results.Count} match{(results.Count == 1 ? "" : "es")} for '{term}':");
    foreach (var (index, entry, folder) in results)
    {
        string tag = string.IsNullOrEmpty(folder) ? "" : $"  ({folder})";
        Console.WriteLine($"  [{index:D2}] {entry.Strings.ReadSafe(PwDefs.TitleField)}{tag}");
    }
}

/// <summary>
/// Interactive: lists entries, asks the user to pick one by number, then
/// moves the chosen entry to the recycle bin (or permanently deletes if already in bin).
/// </summary>
static void DeleteEntry(KeePassService keepass, PwGroup scope)
{
    var entries = keepass.GetEntries(scope);
    if (entries.Count == 0) { Console.WriteLine("  No entries in this folder."); return; }

    keepass.ListGroup(scope);

    Console.Write($"\nEntry number to delete (1–{entries.Count}): ");
    if (!int.TryParse(Console.ReadLine(), out int num) || num < 1 || num > entries.Count)
    {
        Console.WriteLine("  Invalid number — nothing deleted.");
        return;
    }

    ConfirmDeleteEntry(keepass, entries[num - 1]);
}

/// <summary>Finds an entry by name prefix and passes it to <see cref="ConfirmDeleteEntry"/>.</summary>
static void DeleteEntryByName(KeePassService keepass, PwGroup scope, string prefix)
{
    var entry = keepass.FindEntryInGroup(scope, prefix);
    if (entry is null) { Console.WriteLine($"  No entry matching '{prefix}' found."); return; }
    ConfirmDeleteEntry(keepass, entry);
}

/// <summary>
/// Asks for confirmation then moves the entry to the recycle bin.
/// If the entry is already in the recycle bin, offers permanent deletion instead.
/// Returns <c>true</c> if the entry was deleted/moved.
/// </summary>
static bool ConfirmDeleteEntry(KeePassService keepass, PwEntry entry)
{
    string title = entry.Strings.ReadSafe(PwDefs.TitleField);

    if (keepass.IsInRecycleBin(entry))
    {
        Console.Write($"\n  Permanently delete '{title}'? This cannot be undone. (y/n): ");
        if (Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) != true)
        {
            Console.WriteLine("  Cancelled.");
            return false;
        }
        keepass.DeleteEntry(entry);
        Console.WriteLine($"  ✓ '{title}' permanently deleted.");
    }
    else
    {
        Console.Write($"\n  Move '{title}' to recycle bin? (y/n): ");
        if (Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) != true)
        {
            Console.WriteLine("  Cancelled.");
            return false;
        }
        keepass.MoveToRecycleBin(entry);
        Console.WriteLine($"  ✓ '{title}' moved to recycle bin.");
    }

    return true;
}

/// <summary>
/// Interactive: lists entries, lets the user pick one by number, then edits it.
/// </summary>
static void EditEntry(KeePassService keepass, PwGroup scope)
{
    var entries = keepass.GetEntries(scope);
    if (entries.Count == 0) { Console.WriteLine("  No entries to edit."); return; }

    keepass.ListGroup(scope);

    Console.Write($"\nEntry number to edit (1–{entries.Count}): ");
    if (!int.TryParse(Console.ReadLine(), out int num) || num < 1 || num > entries.Count)
    {
        Console.WriteLine("  Invalid number — no changes made.");
        return;
    }

    EditViewedEntry(keepass, entries[num - 1]);
}

/// <summary>
/// Prompts the user to update each field of <paramref name="entry"/>.
/// Pressing Enter on a field keeps its current value.
/// </summary>
static void EditViewedEntry(KeePassService keepass, PwEntry entry)
{
    string curTitle    = entry.Strings.ReadSafe(PwDefs.TitleField);
    string curUsername = entry.Strings.ReadSafe(PwDefs.UserNameField);
    string curUrl      = entry.Strings.ReadSafe(PwDefs.UrlField);
    string curNotes    = entry.Strings.ReadSafe(PwDefs.NotesField);

    Console.WriteLine($"\nEditing: {curTitle}");
    Console.WriteLine("  (Press Enter on any field to keep the current value)\n");

    string? title    = NullIfEmpty(ConsoleHelper.Prompt("Title",    curTitle));
    string? username = NullIfEmpty(ConsoleHelper.Prompt("Username", curUsername));
    string? url      = NullIfEmpty(ConsoleHelper.Prompt("URL",      curUrl));
    string? notes    = NullIfEmpty(ConsoleHelper.Prompt("Notes",    curNotes));

    Console.Write("New password (Enter = keep current): ");
    string? password = NullIfEmpty(ConsoleHelper.ReadPassword());

    keepass.UpdateEntry(entry, title, username, password, url, notes);
    Console.WriteLine("\n  ✓ Entry updated in memory.");
}

/// <summary>
/// Serialises the modified database to a MemoryStream and uploads it to Drive.
/// </summary>
static async Task UploadChangesAsync(
    KeePassService     keepass,
    GoogleDriveService drive,
    string             fileId,
    string             fileName)
{
    Console.WriteLine("\nSerialising database…");
    MemoryStream? updated = keepass.SaveToStream();

    if (updated is null)
    {
        Console.WriteLine("  Nothing to upload (no changes were detected).");
        return;
    }

    Console.WriteLine($"Uploading '{fileName}' ({updated.Length:N0} bytes) to Google Drive…");
    try
    {
        await drive.UploadFileAsync(fileId, fileName, updated);
        Console.WriteLine($"  ✓ '{fileName}' uploaded successfully.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  ERROR: Upload failed — {ex.Message}");
    }
    finally
    {
        await updated.DisposeAsync();
    }
}

static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
