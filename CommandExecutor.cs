using KeePassLib;
using mykeepass.Helpers;
using mykeepass.Parsing;
using mykeepass.Services;
using mykeepass.UI;

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
    string             fileName,
    IUserInteraction   ui)
{
    private PwGroup  _currentGroup = keepass.RootGroup;
    private PwEntry? _viewedEntry  = null;

    /// <summary>The group the user is currently browsing.</summary>
    public PwGroup  CurrentGroup => _currentGroup;

    /// <summary>The entry the user is currently inspecting, or <c>null</c> in folder view.</summary>
    public PwEntry? ViewedEntry  => _viewedEntry;

    /// <summary>
    /// Fired whenever navigation state changes (group entered/exited, entry opened/closed,
    /// entry added or deleted). TUI subscribers use this to refresh the tree and help panel.
    /// </summary>
    public event Action? StateChanged;

    private void NotifyStateChanged() => StateChanged?.Invoke();

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
                await HandleDeleteFolder(df);
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
                ui.WriteLine("  Unknown command.");
                return true;
        }
    }

    // ── Command handlers ──────────────────────────────────────────────────────

    private void HandleBack()
    {
        if (_viewedEntry is not null)
        {
            _viewedEntry = null;
            NotifyStateChanged();
        }
        else if (!ReferenceEquals(_currentGroup, keepass.RootGroup))
        {
            _currentGroup = _currentGroup.ParentGroup ?? keepass.RootGroup;
            ui.WriteLine($"  Back to '{_currentGroup.Name}'.");
            NotifyStateChanged();
        }
        else
        {
            ui.WriteLine("  Already at root.");
        }
    }

    private void HandleList()
    {
        if (_viewedEntry is not null) { ui.WriteLine("  Unknown command."); return; }
        keepass.ListGroup(_currentGroup, ui);
    }

    private void HandleSelect(SelectCommand cmd)
    {
        if (_viewedEntry is not null)
        {
            ui.WriteLine("  Type 'back' to exit the entry view first.");
            return;
        }

        switch (cmd.Target)
        {
            case SelectTarget.Auto:
            {
                if (string.IsNullOrEmpty(cmd.Name)) { ui.WriteLine("  Specify a name."); return; }
                var matchFolder = keepass.FindChildGroup(_currentGroup, cmd.Name);
                var matchEntry  = keepass.FindEntryInGroup(_currentGroup, cmd.Name);

                if (matchFolder is null && matchEntry is null)
                    ui.WriteLine($"  Nothing matching '{cmd.Name}' found in this folder.");
                else if (matchFolder is not null && matchEntry is not null)
                {
                    ui.WriteLine($"  Ambiguous — both a folder and an entry match '{cmd.Name}':");
                    ui.WriteLine($"    folder : {matchFolder.Name}");
                    ui.WriteLine($"    entry  : {matchEntry.Strings.ReadSafe(PwDefs.TitleField)}");
                    ui.WriteLine($"  Use 'select folder {cmd.Name}' or 'select entry {cmd.Name}' to disambiguate.");
                }
                else if (matchFolder is not null)
                {
                    _currentGroup = matchFolder;
                    ui.WriteLine($"  Entered '{matchFolder.Name}'.");
                    NotifyStateChanged();
                }
                else
                {
                    _viewedEntry = matchEntry;
                    NotifyStateChanged();
                }
                break;
            }

            case SelectTarget.Folder:
            {
                if (string.IsNullOrEmpty(cmd.Name)) { ui.WriteLine("  Please specify a folder name."); return; }
                var target = keepass.FindChildGroup(_currentGroup, cmd.Name);
                if (target is null) ui.WriteLine($"  No subfolder matching '{cmd.Name}' found.");
                else
                {
                    _currentGroup = target;
                    ui.WriteLine($"  Entered '{target.Name}'.");
                    NotifyStateChanged();
                }
                break;
            }

            case SelectTarget.Entry:
            {
                if (string.IsNullOrEmpty(cmd.Name)) { ui.WriteLine("  Please specify an entry name."); return; }
                var target = keepass.FindEntryInGroup(_currentGroup, cmd.Name);
                if (target is null) ui.WriteLine($"  No entry matching '{cmd.Name}' found.");
                else
                {
                    _viewedEntry = target;
                    NotifyStateChanged();
                }
                break;
            }
        }
    }

    private void HandleSetField(SetFieldCommand cmd)
    {
        if (_viewedEntry is null)
        {
            ui.WriteLine("  Open an entry first, then set its fields.");
            ui.WriteLine("  e.g.: select gmail  →  set password to S3cr3t");
            ui.WriteLine("  e.g.: select gmail  →  add password with value S3cr3t");
            return;
        }

        if (string.IsNullOrEmpty(cmd.Key))
        {
            ui.WriteLine("  Field name cannot be empty.  e.g.: set password to S3cr3t");
            return;
        }

        keepass.SetEntryField(_viewedEntry, cmd.Key, cmd.Value);
        ui.WriteLine($"  ✓ '{cmd.Key}' set.");
        NotifyStateChanged();
    }

    private void HandleAttachFile(AttachFileCommand cmd)
    {
        if (_viewedEntry is null)
        {
            ui.WriteLine("  Open an entry first, then set its fields from a file.");
            ui.WriteLine("  e.g.: select gmail  →  set password to value from /path/to/secret.txt");
            return;
        }

        if (string.IsNullOrEmpty(cmd.Key))
        {
            ui.WriteLine("  Field name cannot be empty.");
            return;
        }

        // Resolve to the canonical absolute path to prevent path-traversal tricks
        // such as ../../sensitive-file being smuggled in via the command.
        string path;
        try   { path = Path.GetFullPath(cmd.FilePath.Trim().Trim('"')); }
        catch { ui.WriteLine("  Invalid file path."); return; }

        if (!File.Exists(path))
        {
            ui.WriteLine($"  File not found: '{path}'");
            return;
        }

        try
        {
            string contents = File.ReadAllText(path).TrimEnd('\r', '\n');
            keepass.SetEntryField(_viewedEntry, cmd.Key, contents);
            ui.WriteLine($"  ✓ '{cmd.Key}' set from '{path}'.");
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            ui.WriteError($"  ERROR: Could not read '{path}' — {ex.Message}");
        }
    }

    private void HandleCopy(CopyCommand cmd)
    {
        if (_viewedEntry is not null)
        {
            // Entry view: copy from the currently viewed entry.
            if (cmd.Field is not null)
                CopyNamedFieldFromEntry(keepass, _viewedEntry, cmd.Field, ui);
            else
                CopyAttributeFromEntry(keepass, _viewedEntry, ui);
        }
        else
        {
            // Folder view.
            if (cmd.Field is not null && cmd.FromEntry is not null)
            {
                var entry = keepass.FindEntryInGroup(_currentGroup, cmd.FromEntry);
                if (entry is null) ui.WriteLine($"  No entry matching '{cmd.FromEntry}' found.");
                else               CopyNamedFieldFromEntry(keepass, entry, cmd.Field, ui);
            }
            else if (cmd.Field is not null)
            {
                ui.WriteLine("  Usage: copy <field> from <entry>   e.g.: copy password from gmail");
                ui.WriteLine("  Or use bare 'copy' for interactive selection.");
            }
            else
            {
                CopyAttribute(keepass, _currentGroup, ui);
            }
        }
    }

    private async Task HandleDelete(DeleteCommand cmd)
    {
        if (_viewedEntry is not null)
        {
            if (cmd.Name is not null)
            {
                // Entry view with a name: delete that specific field.
                if (keepass.ResolveFieldAlias(cmd.Name) == PwDefs.TitleField)
                { ui.WriteLine("  Title is required and cannot be deleted."); return; }

                if (!keepass.RemoveEntryField(_viewedEntry, cmd.Name))
                    ui.WriteLine($"  Field '{cmd.Name}' not found.");
                else
                {
                    ui.WriteLine($"  ✓ Field '{cmd.Name}' removed.");
                    NotifyStateChanged();
                }
            }
            else
            {
                // Entry view, no name: delete the entry itself.
                if (await ConfirmDeleteEntry(keepass, _viewedEntry, ui))
                {
                    _viewedEntry = null;
                    NotifyStateChanged();
                }
            }
        }
        else if (cmd.Name is not null)
        {
            DeleteEntryByName(keepass, _currentGroup, cmd.Name, ui);
        }
        else
        {
            DeleteEntry(keepass, _currentGroup, ui);
        }
    }

    private async Task HandleDeleteFolder(DeleteFolderCommand cmd)
    {
        if (_viewedEntry is not null) { ui.WriteLine("  Unknown command."); return; }

        var group = keepass.FindChildGroup(_currentGroup, cmd.Name);
        if (group is null) { ui.WriteLine($"  No folder matching '{cmd.Name}' found."); return; }

        if (keepass.IsGroupInRecycleBin(group))
        {
            if (!await ui.ConfirmAsync($"Permanently delete folder '{group.Name}' and all its contents? This cannot be undone."))
            { ui.WriteLine("  Cancelled."); return; }
            keepass.DeleteGroup(group);
            ui.WriteLine($"  ✓ Folder '{group.Name}' permanently deleted.");
            NotifyStateChanged();
        }
        else
        {
            if (!await ui.ConfirmAsync($"Move folder '{group.Name}' to recycle bin?"))
            { ui.WriteLine("  Cancelled."); return; }
            keepass.MoveGroupToRecycleBin(group);
            ui.WriteLine($"  ✓ Folder '{group.Name}' moved to recycle bin.");
            NotifyStateChanged();
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
            if (dest is null) { ui.WriteLine($"  No folder matching '{destName}' found."); return; }
        }

        if (_viewedEntry is not null)
        {
            // Entry view: "move to <dest>" relocates the current entry.
            if (cmd.Name is not null) { ui.WriteLine("  Unknown command."); return; }
            string title = _viewedEntry.Strings.ReadSafe(PwDefs.TitleField);
            keepass.MoveEntryToGroup(_viewedEntry, dest);
            ui.WriteLine($"  ✓ '{title}' moved to '{dest.Name}'.");
            _viewedEntry = null;
            NotifyStateChanged();
            return;
        }

        if (cmd.IsFolder)
        {
            var group = keepass.FindChildGroup(_currentGroup, cmd.Name!);
            if (group is null) { ui.WriteLine($"  No folder matching '{cmd.Name}' found."); return; }
            if (KeePassService.WouldCreateCycle(group, dest))
            { ui.WriteLine("  Cannot move a folder into itself or one of its subfolders."); return; }
            keepass.MoveGroupToGroup(group, dest);
            ui.WriteLine($"  ✓ Folder '{group.Name}' moved to '{dest.Name}'.");
            NotifyStateChanged();
            return;
        }

        // Auto-detect: entry first, then folder.
        var entry = keepass.FindEntryInGroup(_currentGroup, cmd.Name!);
        if (entry is not null)
        {
            keepass.MoveEntryToGroup(entry, dest);
            ui.WriteLine($"  ✓ '{entry.Strings.ReadSafe(PwDefs.TitleField)}' moved to '{dest.Name}'.");
            NotifyStateChanged();
            return;
        }

        var subgroup = keepass.FindChildGroup(_currentGroup, cmd.Name!);
        if (subgroup is null) { ui.WriteLine($"  Nothing matching '{cmd.Name}' found."); return; }
        if (KeePassService.WouldCreateCycle(subgroup, dest))
        { ui.WriteLine("  Cannot move a folder into itself or one of its subfolders."); return; }
        keepass.MoveGroupToGroup(subgroup, dest);
        ui.WriteLine($"  ✓ Folder '{subgroup.Name}' moved to '{dest.Name}'.");
        NotifyStateChanged();
    }

    private void HandleRename(RenameCommand cmd)
    {
        string newName = cmd.NewName.Trim();
        if (string.IsNullOrEmpty(newName)) { ui.WriteLine("  New name cannot be empty."); return; }

        if (_viewedEntry is not null)
        {
            // Entry view: rename the entry's title.
            string oldTitle = _viewedEntry.Strings.ReadSafe(PwDefs.TitleField);
            keepass.SetEntryField(_viewedEntry, PwDefs.TitleField, newName);
            ui.WriteLine($"  ✓ Renamed '{oldTitle}' → '{newName}'.");
            return;
        }

        if (cmd.Name is null)
        {
            // Rename the current folder.
            string oldName = _currentGroup.Name;
            keepass.RenameGroup(_currentGroup, newName);
            ui.WriteLine($"  ✓ Renamed folder '{oldName}' → '{newName}'.");
            NotifyStateChanged();
            return;
        }

        if (cmd.IsFolder)
        {
            var group = keepass.FindChildGroup(_currentGroup, cmd.Name);
            if (group is null) { ui.WriteLine($"  No folder matching '{cmd.Name}' found."); return; }
            string old = group.Name;
            keepass.RenameGroup(group, newName);
            ui.WriteLine($"  ✓ Renamed folder '{old}' → '{newName}'.");
            NotifyStateChanged();
            return;
        }

        // Auto-detect: entry first, then folder.
        var namedEntry = keepass.FindEntryInGroup(_currentGroup, cmd.Name);
        if (namedEntry is not null)
        {
            string old = namedEntry.Strings.ReadSafe(PwDefs.TitleField);
            keepass.SetEntryField(namedEntry, PwDefs.TitleField, newName);
            ui.WriteLine($"  ✓ Renamed '{old}' → '{newName}'.");
            return;
        }

        var namedGroup = keepass.FindChildGroup(_currentGroup, cmd.Name);
        if (namedGroup is null) { ui.WriteLine($"  Nothing matching '{cmd.Name}' found."); return; }
        string oldFolderName = namedGroup.Name;
        keepass.RenameGroup(namedGroup, newName);
        ui.WriteLine($"  ✓ Renamed folder '{oldFolderName}' → '{newName}'.");
        NotifyStateChanged();
    }

    private void HandleEdit(EditCommand cmd)
    {
        if (_viewedEntry is not null)
        {
            // Entry view: edit the currently viewed entry.
            EditViewedEntry(keepass, _viewedEntry, ui);
        }
        else if (cmd.Name is not null)
        {
            var entry = keepass.FindEntryInGroup(_currentGroup, cmd.Name);
            if (entry is null)
                ui.WriteLine($"  No entry matching '{cmd.Name}' found.");
            else
            {
                _viewedEntry = entry;
                EditViewedEntry(keepass, entry, ui);
                NotifyStateChanged();
            }
        }
        else
        {
            EditEntry(keepass, _currentGroup, ui);
        }
    }

    private void HandleAddEntry(AddEntryCommand cmd)
    {
        if (_viewedEntry is not null) { ui.WriteLine("  Unknown command."); return; }

        if (cmd.Name is not null)
        {
            _viewedEntry = keepass.AddQuickEntry(_currentGroup, cmd.Name);
            ui.WriteLine($"  ✓ Entry '{cmd.Name}' created. Use 'set <field> to <val>' to fill in fields.");
            NotifyStateChanged();
        }
        else
        {
            AddEntry(keepass, _currentGroup, ui);
            NotifyStateChanged();
        }
    }

    private void HandleAddFolder(AddFolderCommand cmd)
    {
        if (_viewedEntry is not null) { ui.WriteLine("  Unknown command."); return; }

        string name = cmd.Name ?? ui.Prompt("\nFolder name").Trim();
        if (string.IsNullOrEmpty(name)) { ui.WriteLine("  Folder name cannot be empty."); return; }

        if (keepass.CreateGroup(_currentGroup, name))
        {
            ui.WriteLine($"  ✓ Folder '{name}' created.");
            NotifyStateChanged();
        }
        else
            ui.WriteLine($"  A folder named '{name}' already exists here.");
    }

    private void HandleSearch(SearchCommand cmd)
    {
        if (_viewedEntry is not null) { ui.WriteLine("  Unknown command."); return; }
        SearchEntries(keepass, _currentGroup, ui, cmd.Term ?? "");
    }

    private void PrintUnknown(UnknownCommand _)
    {
        if (_viewedEntry is not null)
            ui.WriteLine("  Unknown command.");
        else
            ui.WriteLine("  Unknown command. Type 'list' to see folder contents.");
    }

    // ── Save / upload ─────────────────────────────────────────────────────────

    private async Task HandleSaveAsync()
    {
        if (!keepass.IsModified)
        {
            ui.WriteLine("  Nothing to save — no changes since last upload.");
            return;
        }
        await UploadChangesAsync();
    }

    private async Task OfferUploadOnExitAsync()
    {
        if (!keepass.IsModified) return;

        if (await ui.ConfirmAsync("You have unsaved changes. Upload to Google Drive?"))
            await UploadChangesAsync();
    }

    private async Task UploadChangesAsync()
    {
        ui.WriteLine("\nSerialising database…");
        MemoryStream? updated = keepass.SaveToStream();

        if (updated is null)
        {
            ui.WriteLine("  Nothing to upload (no changes were detected).");
            return;
        }

        ui.WriteLine($"Uploading '{fileName}' ({updated.Length:N0} bytes) to Google Drive…");
        try
        {
            await drive.UploadFileAsync(fileId, fileName, updated);
            ui.WriteLine($"  ✓ '{fileName}' uploaded successfully.");
            keepass.MarkSaved();
        }
        catch (Exception ex)
        {
            ui.WriteError($"  ERROR: Upload failed — {ex.Message}");
        }
        finally
        {
            await updated.DisposeAsync();
        }
    }

    // ── Static display / UI helpers ───────────────────────────────────────────

    /// <summary>Prints all fields of <paramref name="entry"/> via <paramref name="ui"/>.</summary>
    internal static void ShowEntry(KeePassService kp, PwEntry entry, IUserInteraction ui)
    {
        var fields = kp.GetEntryFields(entry);
        ui.WriteLine();
        foreach (var (name, value, isProtected, isCustom) in fields)
        {
            string display = isProtected ? "●●●●●●●●" : value;
            string tag     = isCustom    ? " [custom]" : string.Empty;
            ui.WriteLine($"  {name,-16}: {display}{tag}");
        }
    }

    /// <summary>Interactively creates a new entry (all fields prompted).</summary>
    private static void AddEntry(KeePassService kp, PwGroup targetGroup, IUserInteraction ui)
    {
        ui.WriteLine();

        string title;
        do
        {
            title = ui.Prompt("Title").Trim();
            if (string.IsNullOrEmpty(title))
                ui.WriteLine("  Title cannot be empty.");
        }
        while (string.IsNullOrEmpty(title));

        string? username = NullIfEmpty(ui.Prompt("Username (Enter to skip)"));
        string? url      = NullIfEmpty(ui.Prompt("Website  (Enter to skip)"));
        string? notes    = NullIfEmpty(ui.Prompt("Notes    (Enter to skip)"));
        string? password = NullIfEmpty(ui.ReadPassword("Password (Enter to skip): "));

        var customFields = new List<(string Key, string Value)>();
        ui.WriteLine("\n  Custom fields (all values are protected). Leave key blank to finish.");
        while (true)
        {
            string key = ui.Prompt("  Key").Trim();
            if (string.IsNullOrEmpty(key)) break;
            string value = ui.ReadPassword("  Value (hidden): ");
            customFields.Add((key, value));
        }

        kp.AddEntryTo(targetGroup, title, username, password, url, notes, customFields);
        ui.WriteLine($"\n  ✓ Entry '{title}' added to '{targetGroup.Name}'.");
    }

    /// <summary>
    /// Copies a named field (case-insensitive key match) from <paramref name="entry"/>
    /// to the clipboard using the secure clipboard helper.
    /// </summary>
    private static void CopyNamedFieldFromEntry(KeePassService kp, PwEntry entry, string fieldKey, IUserInteraction ui)
    {
        var fields = kp.GetEntryFields(entry);
        int idx = -1;
        for (int i = 0; i < fields.Count; i++)
            if (fields[i].Name.Equals(fieldKey, StringComparison.OrdinalIgnoreCase))
            { idx = i; break; }

        if (idx < 0)
        {
            ui.WriteLine($"  Field '{fieldKey}' not found.");
            if (fields.Count > 0)
                ui.WriteLine($"  Available: {string.Join(", ", fields.Select(f => f.Name))}");
            return;
        }

        ClipboardHelper.SetSecureText(fields[idx].Value);
        ui.WriteLine($"  ✓ '{fields[idx].Name}' copied. Clipboard clears in 60 s.");
    }

    /// <summary>
    /// Interactive: shows fields of <paramref name="entry"/>, lets the user pick one,
    /// and copies it to the clipboard.
    /// </summary>
    private static void CopyAttributeFromEntry(KeePassService kp, PwEntry entry, IUserInteraction ui)
    {
        var fields = kp.GetEntryFields(entry);
        if (fields.Count == 0) { ui.WriteLine("  Entry has no fields."); return; }

        var items = fields.Select(f =>
        {
            string display = f.IsProtected ? "●●●●●●●●" : f.Value;
            string tag     = f.IsCustom    ? " [custom]" : string.Empty;
            return $"{f.Name,-16}: {display}{tag}";
        }).ToList();

        ui.WriteLine($"\n  Fields in '{entry.Strings.ReadSafe(PwDefs.TitleField)}':");
        int choice = ui.PickFromList($"Field to copy", items);
        if (choice < 1)
        {
            ui.WriteLine("  Nothing copied.");
            return;
        }

        var chosen = fields[choice - 1];
        ClipboardHelper.SetSecureText(chosen.Value);
        ui.WriteLine($"  ✓ '{chosen.Name}' copied. Clipboard clears in 60 s.");
    }

    /// <summary>
    /// Interactive: lists entries in <paramref name="scope"/>, lets the user pick one,
    /// then copy a field.
    /// </summary>
    private static void CopyAttribute(KeePassService kp, PwGroup scope, IUserInteraction ui)
    {
        var entries = kp.GetEntries(scope);
        if (entries.Count == 0) { ui.WriteLine("  No entries in this folder."); return; }

        kp.ListGroup(scope, ui);

        var items = entries.Select(e => e.Strings.ReadSafe(PwDefs.TitleField)).ToList();
        int choice = ui.PickFromList($"Entry to copy from", items);
        if (choice < 1)
        {
            ui.WriteLine("  Nothing copied.");
            return;
        }

        CopyAttributeFromEntry(kp, entries[choice - 1], ui);
    }

    /// <summary>
    /// Searches entries within <paramref name="scope"/>.
    /// If <paramref name="term"/> is empty the user is prompted for it.
    /// </summary>
    private static void SearchEntries(KeePassService kp, PwGroup scope, IUserInteraction ui, string term = "")
    {
        if (string.IsNullOrEmpty(term))
            term = ui.Prompt("\nSearch").Trim();
        if (string.IsNullOrEmpty(term)) { ui.WriteLine("  Empty search term."); return; }

        var results = kp.Search(term, scope);

        if (results.Count == 0) { ui.WriteLine($"  No entries matched '{term}'."); return; }

        ui.WriteLine($"\n  {results.Count} match{(results.Count == 1 ? "" : "es")} for '{term}':");
        foreach (var (index, entry, folder) in results)
        {
            string tag = string.IsNullOrEmpty(folder) ? "" : $"  ({folder})";
            ui.WriteLine($"  [{index:D2}] {entry.Strings.ReadSafe(PwDefs.TitleField)}{tag}");
        }
    }

    /// <summary>
    /// Interactive: lists entries, asks the user to pick one by number, then
    /// moves the chosen entry to the recycle bin (or permanently deletes if already in bin).
    /// </summary>
    private static async Task DeleteEntry(KeePassService kp, PwGroup scope, IUserInteraction ui)
    {
        var entries = kp.GetEntries(scope);
        if (entries.Count == 0) { ui.WriteLine("  No entries in this folder."); return; }

        kp.ListGroup(scope, ui);

        var items = entries.Select(e => e.Strings.ReadSafe(PwDefs.TitleField)).ToList();
        int choice = ui.PickFromList("Entry number to delete", items);
        if (choice < 1)
        {
            ui.WriteLine("  Nothing deleted.");
            return;
        }

        await ConfirmDeleteEntry(kp, entries[choice - 1], ui);
    }

    /// <summary>Finds an entry by name prefix and passes it to <see cref="ConfirmDeleteEntry"/>.</summary>
    private static async Task DeleteEntryByName(KeePassService kp, PwGroup scope, string prefix, IUserInteraction ui)
    {
        var entry = kp.FindEntryInGroup(scope, prefix);
        if (entry is null) { ui.WriteLine($"  No entry matching '{prefix}' found."); return; }
        await ConfirmDeleteEntry(kp, entry, ui);
    }

    /// <summary>
    /// Asks for confirmation then moves the entry to the recycle bin.
    /// If already in the recycle bin, offers permanent deletion instead.
    /// Returns <c>true</c> if the entry was deleted/moved.
    /// </summary>
    private static async Task<bool> ConfirmDeleteEntry(KeePassService kp, PwEntry entry, IUserInteraction ui)
    {
        string title = entry.Strings.ReadSafe(PwDefs.TitleField);

        if (kp.IsInRecycleBin(entry))
        {
            if (!await ui.ConfirmAsync($"Permanently delete '{title}'? This cannot be undone."))
            {
                ui.WriteLine("  Cancelled.");
                return false;
            }
            kp.DeleteEntry(entry);
            ui.WriteLine($"  ✓ '{title}' permanently deleted.");
        }
        else
        {
            if (!await ui.ConfirmAsync($"Move '{title}' to recycle bin?"))
            {
                ui.WriteLine("  Cancelled.");
                return false;
            }
            kp.MoveToRecycleBin(entry);
            ui.WriteLine($"  ✓ '{title}' moved to recycle bin.");
        }

        return true;
    }

    /// <summary>Interactive: lists entries, lets the user pick one by number, then edits it.</summary>
    private static void EditEntry(KeePassService kp, PwGroup scope, IUserInteraction ui)
    {
        var entries = kp.GetEntries(scope);
        if (entries.Count == 0) { ui.WriteLine("  No entries to edit."); return; }

        kp.ListGroup(scope, ui);

        var items = entries.Select(e => e.Strings.ReadSafe(PwDefs.TitleField)).ToList();
        int choice = ui.PickFromList("Entry number to edit", items);
        if (choice < 1)
        {
            ui.WriteLine("  No changes made.");
            return;
        }

        EditViewedEntry(kp, entries[choice - 1], ui);
    }

    /// <summary>
    /// Prompts the user to update each field of <paramref name="entry"/>.
    /// Pressing Enter on a field keeps its current value.
    /// </summary>
    private static void EditViewedEntry(KeePassService kp, PwEntry entry, IUserInteraction ui)
    {
        string curTitle    = entry.Strings.ReadSafe(PwDefs.TitleField);
        string curUsername = entry.Strings.ReadSafe(PwDefs.UserNameField);
        string curUrl      = entry.Strings.ReadSafe(PwDefs.UrlField);
        string curNotes    = entry.Strings.ReadSafe(PwDefs.NotesField);

        ui.WriteLine($"\nEditing: {curTitle}");
        ui.WriteLine("  (Press Enter on any field to keep the current value)\n");

        string? title    = NullIfEmpty(ui.Prompt("Title",    curTitle));
        string? username = NullIfEmpty(ui.Prompt("Username", curUsername));
        string? url      = NullIfEmpty(ui.Prompt("URL",      curUrl));
        string? notes    = NullIfEmpty(ui.Prompt("Notes",    curNotes));
        string? password = NullIfEmpty(ui.ReadPassword("New password (Enter = keep current): "));

        kp.UpdateEntry(entry, title, username, password, url, notes);
        ui.WriteLine("\n  ✓ Entry updated in memory.");
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
