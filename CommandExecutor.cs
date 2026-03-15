using KeePassLib;
using mykeepass.Helpers;
using mykeepass.Parsing;
using mykeepass.Services;

namespace mykeepass;

/// <summary>
/// Owns the interactive session state (current group / viewed entry) and
/// dispatches typed <see cref="ICommand"/> values to the appropriate handler.
/// Changes accumulate in memory throughout the session; the user is offered an
/// upload only once — when they exit.
/// </summary>
internal sealed class CommandExecutor(
    KeePassService     keepass,
    GoogleDriveService drive,
    string             fileId,
    string             fileName)
{
    private PwGroup  _currentGroup = keepass.RootGroup;
    private PwEntry? _viewedEntry  = null;

    /// <summary>The group the user is currently browsing.</summary>
    public PwGroup  CurrentGroup => _currentGroup;

    /// <summary>The entry the user is currently inspecting, or <c>null</c> in folder view.</summary>
    public PwEntry? ViewedEntry  => _viewedEntry;

    // ── Main dispatch ─────────────────────────────────────────────────────────

    /// <summary>
    /// Executes <paramref name="command"/> and returns <c>false</c> when the
    /// user has requested to exit the application.
    /// </summary>
    public async Task<bool> ExecuteAsync(ICommand command)
    {
        switch (command)
        {
            case EmptyCommand:
                return true;

            case UnknownCommand unk:
                PrintUnknown(unk);
                return true;

            case ExitCommand:
                await OfferUploadOnExitAsync();
                return false;

            case BackCommand:
                HandleBack();
                return true;

            case ListCommand:
                HandleList();
                return true;

            case SaveCommand:
                await HandleSaveAsync();
                return true;

            case SelectCommand sel:
                HandleSelect(sel);
                return true;

            case SetFieldCommand sf:
                HandleSetField(sf);
                return true;

            case AttachFileCommand af:
                HandleAttachFile(af);
                return true;

            case CopyCommand cp:
                HandleCopy(cp);
                return true;

            case DeleteFolderCommand df:
                HandleDeleteFolder(df);
                return true;

            case DeleteCommand del:
                HandleDelete(del);
                return true;

            case MoveCommand mv:
                HandleMove(mv);
                return true;

            case RenameCommand ren:
                HandleRename(ren);
                return true;

            case EditCommand edit:
                HandleEdit(edit);
                return true;

            case AddEntryCommand ae:
                HandleAddEntry(ae);
                return true;

            case AddFolderCommand af:
                HandleAddFolder(af);
                return true;

            case SearchCommand srch:
                HandleSearch(srch);
                return true;

            default:
                Console.WriteLine("  Unknown command.");
                return true;
        }
    }

    // ── Command handlers ──────────────────────────────────────────────────────

    private void HandleBack()
    {
        if (_viewedEntry is not null)
        {
            _viewedEntry = null;
        }
        else if (!ReferenceEquals(_currentGroup, keepass.RootGroup))
        {
            _currentGroup = _currentGroup.ParentGroup ?? keepass.RootGroup;
            Console.WriteLine($"  Back to '{_currentGroup.Name}'.");
        }
        else
        {
            Console.WriteLine("  Already at root.");
        }
    }

    private void HandleList()
    {
        if (_viewedEntry is not null) { Console.WriteLine("  Unknown command."); return; }
        keepass.ListGroup(_currentGroup);
    }

    private void HandleSelect(SelectCommand cmd)
    {
        if (_viewedEntry is not null)
        {
            Console.WriteLine("  Type 'back' to exit the entry view first.");
            return;
        }

        switch (cmd.Target)
        {
            case SelectTarget.Auto:
            {
                if (string.IsNullOrEmpty(cmd.Name)) { Console.WriteLine("  Specify a name."); return; }
                var matchFolder = keepass.FindChildGroup(_currentGroup, cmd.Name);
                var matchEntry  = keepass.FindEntryInGroup(_currentGroup, cmd.Name);

                if (matchFolder is null && matchEntry is null)
                    Console.WriteLine($"  Nothing matching '{cmd.Name}' found in this folder.");
                else if (matchFolder is not null && matchEntry is not null)
                {
                    Console.WriteLine($"  Ambiguous — both a folder and an entry match '{cmd.Name}':");
                    Console.WriteLine($"    folder : {matchFolder.Name}");
                    Console.WriteLine($"    entry  : {matchEntry.Strings.ReadSafe(PwDefs.TitleField)}");
                    Console.WriteLine($"  Use 'select folder {cmd.Name}' or 'select entry {cmd.Name}' to disambiguate.");
                }
                else if (matchFolder is not null)
                {
                    _currentGroup = matchFolder;
                    Console.WriteLine($"  Entered '{matchFolder.Name}'.");
                }
                else
                {
                    _viewedEntry = matchEntry;
                }
                break;
            }

            case SelectTarget.Folder:
            {
                if (string.IsNullOrEmpty(cmd.Name)) { Console.WriteLine("  Please specify a folder name."); return; }
                var target = keepass.FindChildGroup(_currentGroup, cmd.Name);
                if (target is null) Console.WriteLine($"  No subfolder matching '{cmd.Name}' found.");
                else { _currentGroup = target; Console.WriteLine($"  Entered '{target.Name}'."); }
                break;
            }

            case SelectTarget.Entry:
            {
                if (string.IsNullOrEmpty(cmd.Name)) { Console.WriteLine("  Please specify an entry name."); return; }
                var target = keepass.FindEntryInGroup(_currentGroup, cmd.Name);
                if (target is null) Console.WriteLine($"  No entry matching '{cmd.Name}' found.");
                else _viewedEntry = target;
                break;
            }
        }
    }

    private void HandleSetField(SetFieldCommand cmd)
    {
        if (_viewedEntry is null)
        {
            Console.WriteLine("  Open an entry first, then set its fields.");
            Console.WriteLine("  e.g.: select gmail  →  set password to S3cr3t");
            Console.WriteLine("  e.g.: select gmail  →  add password with value S3cr3t");
            return;
        }

        if (string.IsNullOrEmpty(cmd.Key))
        {
            Console.WriteLine("  Field name cannot be empty.  e.g.: set password to S3cr3t");
            return;
        }

        keepass.SetEntryField(_viewedEntry, cmd.Key, cmd.Value);
        Console.WriteLine($"  ✓ '{cmd.Key}' set.");
    }

    private void HandleAttachFile(AttachFileCommand cmd)
    {
        if (_viewedEntry is null)
        {
            Console.WriteLine("  Open an entry first, then set its fields from a file.");
            Console.WriteLine("  e.g.: select gmail  →  set password to value from /path/to/secret.txt");
            return;
        }

        if (string.IsNullOrEmpty(cmd.Key))
        {
            Console.WriteLine("  Field name cannot be empty.");
            return;
        }

        string path = cmd.FilePath.Trim().Trim('"');
        if (!File.Exists(path))
        {
            Console.WriteLine($"  File not found: '{path}'");
            return;
        }

        try
        {
            string contents = File.ReadAllText(path).TrimEnd('\r', '\n');
            keepass.SetEntryField(_viewedEntry, cmd.Key, contents);
            Console.WriteLine($"  ✓ '{cmd.Key}' set from '{path}'.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  ERROR: Could not read '{path}' — {ex.Message}");
        }
    }

    private void HandleCopy(CopyCommand cmd)
    {
        if (_viewedEntry is not null)
        {
            // Entry view: copy from the currently viewed entry.
            if (cmd.Field is not null)
                CopyNamedFieldFromEntry(keepass, _viewedEntry, cmd.Field);
            else
                CopyAttributeFromEntry(keepass, _viewedEntry);
        }
        else
        {
            // Folder view.
            if (cmd.Field is not null && cmd.FromEntry is not null)
            {
                var entry = keepass.FindEntryInGroup(_currentGroup, cmd.FromEntry);
                if (entry is null) Console.WriteLine($"  No entry matching '{cmd.FromEntry}' found.");
                else               CopyNamedFieldFromEntry(keepass, entry, cmd.Field);
            }
            else if (cmd.Field is not null)
            {
                Console.WriteLine("  Usage: copy <field> from <entry>   e.g.: copy password from gmail");
                Console.WriteLine("  Or use bare 'copy' for interactive selection.");
            }
            else
            {
                CopyAttribute(keepass, _currentGroup);
            }
        }
    }

    private void HandleDelete(DeleteCommand cmd)
    {
        if (_viewedEntry is not null)
        {
            if (cmd.Name is not null)
            {
                // Entry view with a name: delete that specific field.
                if (keepass.ResolveFieldAlias(cmd.Name) == PwDefs.TitleField)
                { Console.WriteLine("  Title is required and cannot be deleted."); return; }

                if (!keepass.RemoveEntryField(_viewedEntry, cmd.Name))
                    Console.WriteLine($"  Field '{cmd.Name}' not found.");
                else
                    Console.WriteLine($"  ✓ Field '{cmd.Name}' removed.");
            }
            else
            {
                // Entry view, no name: delete the entry itself.
                if (ConfirmDeleteEntry(keepass, _viewedEntry))
                    _viewedEntry = null;
            }
        }
        else if (cmd.Name is not null)
        {
            DeleteEntryByName(keepass, _currentGroup, cmd.Name);
        }
        else
        {
            DeleteEntry(keepass, _currentGroup);
        }
    }

    private void HandleDeleteFolder(DeleteFolderCommand cmd)
    {
        if (_viewedEntry is not null) { Console.WriteLine("  Unknown command."); return; }

        var group = keepass.FindChildGroup(_currentGroup, cmd.Name);
        if (group is null) { Console.WriteLine($"  No folder matching '{cmd.Name}' found."); return; }

        if (keepass.IsGroupInRecycleBin(group))
        {
            Console.Write($"\n  Permanently delete folder '{group.Name}' and all its contents? This cannot be undone. (y/n): ");
            if (Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) != true)
            { Console.WriteLine("  Cancelled."); return; }
            keepass.DeleteGroup(group);
            Console.WriteLine($"  ✓ Folder '{group.Name}' permanently deleted.");
        }
        else
        {
            Console.Write($"\n  Move folder '{group.Name}' to recycle bin? (y/n): ");
            if (Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) != true)
            { Console.WriteLine("  Cancelled."); return; }
            keepass.MoveGroupToRecycleBin(group);
            Console.WriteLine($"  ✓ Folder '{group.Name}' moved to recycle bin.");
        }
    }

    private void HandleMove(MoveCommand cmd)
    {
        string destName = cmd.Destination.Trim();
        PwGroup dest;
        if (destName is "/" or "root")
            dest = keepass.RootGroup;
        else
        {
            dest = keepass.FindGroup(destName);
            if (dest is null) { Console.WriteLine($"  No folder matching '{destName}' found."); return; }
        }

        if (_viewedEntry is not null)
        {
            // Entry view: "move to <dest>" relocates the current entry.
            if (cmd.Name is not null) { Console.WriteLine("  Unknown command."); return; }
            string title = _viewedEntry.Strings.ReadSafe(PwDefs.TitleField);
            keepass.MoveEntryToGroup(_viewedEntry, dest);
            Console.WriteLine($"  ✓ '{title}' moved to '{dest.Name}'.");
            _viewedEntry = null;
            return;
        }

        if (cmd.IsFolder)
        {
            var group = keepass.FindChildGroup(_currentGroup, cmd.Name!);
            if (group is null) { Console.WriteLine($"  No folder matching '{cmd.Name}' found."); return; }
            if (KeePassService.WouldCreateCycle(group, dest))
            { Console.WriteLine("  Cannot move a folder into itself or one of its subfolders."); return; }
            keepass.MoveGroupToGroup(group, dest);
            Console.WriteLine($"  ✓ Folder '{group.Name}' moved to '{dest.Name}'.");
            return;
        }

        // Auto-detect: entry first, then folder.
        var entry = keepass.FindEntryInGroup(_currentGroup, cmd.Name!);
        if (entry is not null)
        {
            keepass.MoveEntryToGroup(entry, dest);
            Console.WriteLine($"  ✓ '{entry.Strings.ReadSafe(PwDefs.TitleField)}' moved to '{dest.Name}'.");
            return;
        }

        var subgroup = keepass.FindChildGroup(_currentGroup, cmd.Name!);
        if (subgroup is null) { Console.WriteLine($"  Nothing matching '{cmd.Name}' found."); return; }
        if (KeePassService.WouldCreateCycle(subgroup, dest))
        { Console.WriteLine("  Cannot move a folder into itself or one of its subfolders."); return; }
        keepass.MoveGroupToGroup(subgroup, dest);
        Console.WriteLine($"  ✓ Folder '{subgroup.Name}' moved to '{dest.Name}'.");
    }

    private void HandleRename(RenameCommand cmd)
    {
        string newName = cmd.NewName.Trim();
        if (string.IsNullOrEmpty(newName)) { Console.WriteLine("  New name cannot be empty."); return; }

        if (_viewedEntry is not null)
        {
            // Entry view: rename the entry's title.
            string oldTitle = _viewedEntry.Strings.ReadSafe(PwDefs.TitleField);
            keepass.SetEntryField(_viewedEntry, PwDefs.TitleField, newName);
            Console.WriteLine($"  ✓ Renamed '{oldTitle}' → '{newName}'.");
            return;
        }

        if (cmd.Name is null)
        {
            // Rename the current folder.
            string oldName = _currentGroup.Name;
            keepass.RenameGroup(_currentGroup, newName);
            Console.WriteLine($"  ✓ Renamed folder '{oldName}' → '{newName}'.");
            return;
        }

        if (cmd.IsFolder)
        {
            var group = keepass.FindChildGroup(_currentGroup, cmd.Name);
            if (group is null) { Console.WriteLine($"  No folder matching '{cmd.Name}' found."); return; }
            string old = group.Name;
            keepass.RenameGroup(group, newName);
            Console.WriteLine($"  ✓ Renamed folder '{old}' → '{newName}'.");
            return;
        }

        // Auto-detect: entry first, then folder.
        var namedEntry = keepass.FindEntryInGroup(_currentGroup, cmd.Name);
        if (namedEntry is not null)
        {
            string old = namedEntry.Strings.ReadSafe(PwDefs.TitleField);
            keepass.SetEntryField(namedEntry, PwDefs.TitleField, newName);
            Console.WriteLine($"  ✓ Renamed '{old}' → '{newName}'.");
            return;
        }

        var namedGroup = keepass.FindChildGroup(_currentGroup, cmd.Name);
        if (namedGroup is null) { Console.WriteLine($"  Nothing matching '{cmd.Name}' found."); return; }
        string oldFolderName = namedGroup.Name;
        keepass.RenameGroup(namedGroup, newName);
        Console.WriteLine($"  ✓ Renamed folder '{oldFolderName}' → '{newName}'.");
    }

    private void HandleEdit(EditCommand cmd)
    {
        if (_viewedEntry is not null)
        {
            // Entry view: edit the currently viewed entry.
            EditViewedEntry(keepass, _viewedEntry);
        }
        else if (cmd.Name is not null)
        {
            var entry = keepass.FindEntryInGroup(_currentGroup, cmd.Name);
            if (entry is null)
                Console.WriteLine($"  No entry matching '{cmd.Name}' found.");
            else
            {
                _viewedEntry = entry;
                EditViewedEntry(keepass, entry);
            }
        }
        else
        {
            EditEntry(keepass, _currentGroup);
        }
    }

    private void HandleAddEntry(AddEntryCommand cmd)
    {
        if (_viewedEntry is not null) { Console.WriteLine("  Unknown command."); return; }

        if (cmd.Name is not null)
        {
            _viewedEntry = keepass.AddQuickEntry(_currentGroup, cmd.Name);
            Console.WriteLine($"  ✓ Entry '{cmd.Name}' created. Use 'set <field> to <val>' to fill in fields.");
        }
        else
        {
            AddEntry(keepass, _currentGroup);
        }
    }

    private void HandleAddFolder(AddFolderCommand cmd)
    {
        if (_viewedEntry is not null) { Console.WriteLine("  Unknown command."); return; }

        string name = cmd.Name ?? ConsoleHelper.Prompt("\nFolder name").Trim();
        if (string.IsNullOrEmpty(name)) { Console.WriteLine("  Folder name cannot be empty."); return; }

        if (keepass.CreateGroup(_currentGroup, name))
            Console.WriteLine($"  ✓ Folder '{name}' created.");
        else
            Console.WriteLine($"  A folder named '{name}' already exists here.");
    }

    private void HandleSearch(SearchCommand cmd)
    {
        if (_viewedEntry is not null) { Console.WriteLine("  Unknown command."); return; }
        SearchEntries(keepass, _currentGroup, cmd.Term ?? "");
    }

    private void PrintUnknown(UnknownCommand _)
    {
        if (_viewedEntry is not null)
            Console.WriteLine("  Unknown command.");
        else
            Console.WriteLine("  Unknown command. Type 'list' to see folder contents.");
    }

    // ── Save / upload ─────────────────────────────────────────────────────────

    private async Task HandleSaveAsync()
    {
        if (!keepass.IsModified)
        {
            Console.WriteLine("  Nothing to save — no changes since last upload.");
            return;
        }
        await UploadChangesAsync();
    }

    private async Task OfferUploadOnExitAsync()
    {
        if (!keepass.IsModified) return;

        Console.Write("\nYou have unsaved changes. Upload to Google Drive? (y/n): ");
        if (Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true)
            await UploadChangesAsync();
    }

    private async Task UploadChangesAsync()
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
            keepass.MarkSaved();
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

    // ── Static display / UI helpers (moved verbatim from Program.cs) ──────────

    /// <summary>Prints all fields of <paramref name="entry"/> to the console.</summary>
    internal static void ShowEntry(KeePassService kp, PwEntry entry)
    {
        var fields = kp.GetEntryFields(entry);
        Console.WriteLine();
        foreach (var (name, value, isProtected, isCustom) in fields)
        {
            string display = isProtected ? "●●●●●●●●" : value;
            string tag     = isCustom    ? " [custom]" : string.Empty;
            Console.WriteLine($"  {name,-16}: {display}{tag}");
        }
    }

    /// <summary>Interactively creates a new entry (all fields prompted).</summary>
    private static void AddEntry(KeePassService kp, PwGroup targetGroup)
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

        kp.AddEntryTo(targetGroup, title, username, password, url, notes, customFields);
        Console.WriteLine($"\n  ✓ Entry '{title}' added to '{targetGroup.Name}'.");
    }

    /// <summary>
    /// Copies a named field (case-insensitive key match) from <paramref name="entry"/>
    /// to the clipboard using the secure clipboard helper.
    /// </summary>
    private static void CopyNamedFieldFromEntry(KeePassService kp, PwEntry entry, string fieldKey)
    {
        var fields = kp.GetEntryFields(entry);
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
    private static void CopyAttributeFromEntry(KeePassService kp, PwEntry entry)
    {
        var fields = kp.GetEntryFields(entry);
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
    private static void CopyAttribute(KeePassService kp, PwGroup scope)
    {
        var entries = kp.GetEntries(scope);
        if (entries.Count == 0) { Console.WriteLine("  No entries in this folder."); return; }

        kp.ListGroup(scope);

        Console.Write($"\nEntry number to copy from (1–{entries.Count}): ");
        if (!int.TryParse(Console.ReadLine(), out int num) || num < 1 || num > entries.Count)
        {
            Console.WriteLine("  Invalid number — nothing copied.");
            return;
        }

        CopyAttributeFromEntry(kp, entries[num - 1]);
    }

    /// <summary>
    /// Searches entries within <paramref name="scope"/>.
    /// If <paramref name="term"/> is empty the user is prompted for it.
    /// </summary>
    private static void SearchEntries(KeePassService kp, PwGroup scope, string term = "")
    {
        if (string.IsNullOrEmpty(term))
            term = ConsoleHelper.Prompt("\nSearch").Trim();
        if (string.IsNullOrEmpty(term)) { Console.WriteLine("  Empty search term."); return; }

        var results = kp.Search(term, scope);

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
    private static void DeleteEntry(KeePassService kp, PwGroup scope)
    {
        var entries = kp.GetEntries(scope);
        if (entries.Count == 0) { Console.WriteLine("  No entries in this folder."); return; }

        kp.ListGroup(scope);

        Console.Write($"\nEntry number to delete (1–{entries.Count}): ");
        if (!int.TryParse(Console.ReadLine(), out int num) || num < 1 || num > entries.Count)
        {
            Console.WriteLine("  Invalid number — nothing deleted.");
            return;
        }

        ConfirmDeleteEntry(kp, entries[num - 1]);
    }

    /// <summary>Finds an entry by name prefix and passes it to <see cref="ConfirmDeleteEntry"/>.</summary>
    private static void DeleteEntryByName(KeePassService kp, PwGroup scope, string prefix)
    {
        var entry = kp.FindEntryInGroup(scope, prefix);
        if (entry is null) { Console.WriteLine($"  No entry matching '{prefix}' found."); return; }
        ConfirmDeleteEntry(kp, entry);
    }

    /// <summary>
    /// Asks for confirmation then moves the entry to the recycle bin.
    /// If already in the recycle bin, offers permanent deletion instead.
    /// Returns <c>true</c> if the entry was deleted/moved.
    /// </summary>
    private static bool ConfirmDeleteEntry(KeePassService kp, PwEntry entry)
    {
        string title = entry.Strings.ReadSafe(PwDefs.TitleField);

        if (kp.IsInRecycleBin(entry))
        {
            Console.Write($"\n  Permanently delete '{title}'? This cannot be undone. (y/n): ");
            if (Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) != true)
            {
                Console.WriteLine("  Cancelled.");
                return false;
            }
            kp.DeleteEntry(entry);
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
            kp.MoveToRecycleBin(entry);
            Console.WriteLine($"  ✓ '{title}' moved to recycle bin.");
        }

        return true;
    }

    /// <summary>Interactive: lists entries, lets the user pick one by number, then edits it.</summary>
    private static void EditEntry(KeePassService kp, PwGroup scope)
    {
        var entries = kp.GetEntries(scope);
        if (entries.Count == 0) { Console.WriteLine("  No entries to edit."); return; }

        kp.ListGroup(scope);

        Console.Write($"\nEntry number to edit (1–{entries.Count}): ");
        if (!int.TryParse(Console.ReadLine(), out int num) || num < 1 || num > entries.Count)
        {
            Console.WriteLine("  Invalid number — no changes made.");
            return;
        }

        EditViewedEntry(kp, entries[num - 1]);
    }

    /// <summary>
    /// Prompts the user to update each field of <paramref name="entry"/>.
    /// Pressing Enter on a field keeps its current value.
    /// </summary>
    private static void EditViewedEntry(KeePassService kp, PwEntry entry)
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

        kp.UpdateEntry(entry, title, username, password, url, notes);
        Console.WriteLine("\n  ✓ Entry updated in memory.");
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
