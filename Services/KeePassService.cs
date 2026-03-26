using KeePassLib;
using KeePassLib.Keys;
using KeePassLib.Security;
using KeePassLib.Serialization;
using mykeepass.UI;
using System.Linq;

namespace mykeepass.Services;

/// <summary>
/// Manages a KeePass 2 (.kdbx) database entirely in memory:
/// decrypt, inspect, update entries, and re-encrypt — the disk is never touched.
/// </summary>
public sealed class KeePassService : IDisposable
{
    private readonly PwDatabase _db = new();
    private bool _modified;

    // ── Constructors ─────────────────────────────────────────────────────────

    /// <summary>
    /// Opens the database using a UTF-8 password byte array.
    /// The <b>caller</b> must zero-fill <paramref name="passwordUtf8"/> after
    /// this constructor returns (success or failure).
    /// KeePassLib copies the bytes internally and handles its own cleanup.
    /// </summary>
    public KeePassService(MemoryStream source, byte[] passwordUtf8, MemoryStream? keyFileStream = null)
        => OpenDatabase(source, new KcpPassword(passwordUtf8), keyFileStream);

    /// <summary>
    /// Opens the database using a plain-text password string.
    /// Prefer the <c>byte[]</c> overload for new code so the caller can
    /// zero-fill the buffer after use.
    /// </summary>
    public KeePassService(MemoryStream source, string masterPassword, MemoryStream? keyFileStream = null)
        => OpenDatabase(source, new KcpPassword(masterPassword), keyFileStream);

    private void OpenDatabase(MemoryStream source, KcpPassword password, MemoryStream? keyFileStream = null)
    {
        var key = new CompositeKey();
        key.AddUserKey(password);

        if (keyFileStream is not null)
        {
            // KcpKeyFile requires a file path; write to a temp file, add the key, then delete it.
            // KeePassLib reads only the key material from the file — no sensitive database content.
            string tempPath = Path.GetTempFileName();
            try
            {
                using (var fs = File.Create(tempPath))
                    keyFileStream.CopyTo(fs);
                key.AddUserKey(new KcpKeyFile(tempPath));
            }
            finally
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
        }

        _db.MasterKey = key;

        try
        {
            using var localCopy = new MemoryStream();
            source.CopyTo(localCopy);
            localCopy.Position = 0;

            var kdbx = new KdbxFile(_db);
            kdbx.Load(localCopy, KdbxFormat.Default, slLogger: null);
        }
        catch (Exception ex)
        {
            _db.Close();
            throw new InvalidOperationException(
                "Could not open the database. " +
                "The master password or key file may be wrong, or the file may be corrupt. " +
                $"({ex.Message})", ex);
        }
    }

    // ── Constants ─────────────────────────────────────────────────────────────

    private static readonly IReadOnlySet<string> StandardKeys = new HashSet<string>
    {
        PwDefs.TitleField, PwDefs.UserNameField,
        PwDefs.PasswordField, PwDefs.UrlField, PwDefs.NotesField,
    };

    /// <summary>
    /// Common aliases that map to standard KeePass field names (case-insensitive key lookup).
    /// Any key not listed here is treated as a custom field.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> FieldAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"]    = PwDefs.TitleField,
            ["name"]     = PwDefs.TitleField,
            ["username"] = PwDefs.UserNameField,
            ["user"]     = PwDefs.UserNameField,
            ["login"]    = PwDefs.UserNameField,
            ["email"]    = PwDefs.UserNameField,
            ["password"] = PwDefs.PasswordField,
            ["pass"]     = PwDefs.PasswordField,
            ["pwd"]      = PwDefs.PasswordField,
            ["url"]      = PwDefs.UrlField,
            ["website"]  = PwDefs.UrlField,
            ["site"]     = PwDefs.UrlField,
            ["link"]     = PwDefs.UrlField,
            ["notes"]    = PwDefs.NotesField,
            ["note"]     = PwDefs.NotesField,
        };

    // ── Navigation ────────────────────────────────────────────────────────────

    public PwGroup RootGroup => _db.RootGroup;

    /// <summary>Flat depth-first list of all entries under <paramref name="group"/>.</summary>
    public List<PwEntry> GetEntries(PwGroup group)
    {
        var list = new List<PwEntry>();
        CollectEntries(group, list);
        return list;
    }

    public List<PwEntry> GetAllEntries() => GetEntries(_db.RootGroup);

    /// <summary>
    /// Finds a direct child group of <paramref name="parent"/> whose name
    /// matches <paramref name="prefix"/> (exact first, then prefix, case-insensitive).
    /// </summary>
    public PwGroup? FindChildGroup(PwGroup parent, string prefix)
    {
        foreach (PwGroup g in parent.Groups)
            if (g.Name.Equals(prefix, StringComparison.OrdinalIgnoreCase)) return g;
        foreach (PwGroup g in parent.Groups)
            if (g.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return g;
        return null;
    }

    /// <summary>
    /// Finds a direct entry in <paramref name="group"/> whose title matches
    /// <paramref name="prefix"/> (exact first, then prefix, case-insensitive).
    /// </summary>
    public PwEntry? FindEntryInGroup(PwGroup group, string prefix)
    {
        foreach (PwEntry e in group.Entries)
            if (e.Strings.ReadSafe(PwDefs.TitleField).Equals(prefix, StringComparison.OrdinalIgnoreCase)) return e;
        foreach (PwEntry e in group.Entries)
            if (e.Strings.ReadSafe(PwDefs.TitleField).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    // ── Display ───────────────────────────────────────────────────────────────

    /// <summary>Prints the full database tree.</summary>
    public void ListAllEntries(IUserInteraction ui) => PrintGroupTree(_db.RootGroup, isRoot: true, ui);

    /// <summary>Prints the tree rooted at <paramref name="group"/>.</summary>
    public void ListGroup(PwGroup group, IUserInteraction ui) => PrintGroupTree(group, isRoot: false, ui);

    private void PrintGroupTree(PwGroup group, bool isRoot, IUserInteraction ui)
    {
        int total = GetEntries(group).Count;

        ui.WriteLine();
        ui.WriteLine("══════════════════════════════════════════════");
        ui.WriteLine(isRoot
            ? $"  {total} entr{(total == 1 ? "y" : "ies")} found"
            : $"  {total} entr{(total == 1 ? "y" : "ies")} in '{group.Name}'");
        ui.WriteLine("══════════════════════════════════════════════\n");

        if (total == 0 && group.Groups.UCount == 0)
        {
            ui.WriteLine("  (empty)");
            return;
        }

        ui.WriteLine($"  ▷ {group.Name}");
        int index = 1;
        PrintTree(group, ref index, prefix: "  ", ui);
        ui.WriteLine();
    }

    private static void PrintTree(PwGroup group, ref int index, string prefix, IUserInteraction ui)
    {
        var entries   = group.Entries.Cast<PwEntry>().ToList();
        var subgroups = group.Groups .Cast<PwGroup>() .ToList();
        int total = entries.Count + subgroups.Count;
        int pos   = 0;

        foreach (var entry in entries)
        {
            bool   last  = ++pos == total;
            string conn  = last ? "└── " : "├── ";
            string title = entry.Strings.ReadSafe(PwDefs.TitleField);
            ui.WriteLine($"{prefix}{conn}◇ [{index++:D2}] {title}");
        }

        foreach (var sub in subgroups)
        {
            bool   last = ++pos == total;
            string conn = last ? "└── " : "├── ";
            string ext  = last ? "    " : "│   ";
            ui.WriteLine($"{prefix}{conn}▷ {sub.Name}");
            PrintTree(sub, ref index, prefix + ext, ui);
        }
    }

    // ── Read: fields ──────────────────────────────────────────────────────────

    /// <summary>Returns all fields of a specific entry.</summary>
    public IReadOnlyList<(string Name, string Value, bool IsProtected, bool IsCustom)>
        GetEntryFields(PwEntry entry)
    {
        var result = new List<(string, string, bool, bool)>();

        void TryAdd(string key, bool isCustom)
        {
            var ps = entry.Strings.Get(key);
            if (ps is null) return;
            string val = ps.ReadString();
            if (!string.IsNullOrEmpty(val) || isCustom)
                result.Add((key, val, ps.IsProtected, isCustom));
        }

        TryAdd(PwDefs.TitleField,    false);
        TryAdd(PwDefs.UserNameField, false);
        TryAdd(PwDefs.PasswordField, false);
        TryAdd(PwDefs.UrlField,      false);
        TryAdd(PwDefs.NotesField,    false);

        foreach (string key in entry.Strings.GetKeys())
            if (!StandardKeys.Contains(key))
                TryAdd(key, isCustom: true);

        return result;
    }

    /// <summary>Returns fields of the entry at zero-based <paramref name="index"/> within <paramref name="scope"/>.</summary>
    public IReadOnlyList<(string Name, string Value, bool IsProtected, bool IsCustom)>
        GetEntryFields(PwGroup scope, int index)
    {
        var entries = GetEntries(scope);
        return index >= 0 && index < entries.Count ? GetEntryFields(entries[index]) : [];
    }

    // ── Read: search ──────────────────────────────────────────────────────────

    /// <summary>Searches entries within <paramref name="scope"/> by title, username, or custom key names.</summary>
    public IReadOnlyList<(int Index, PwEntry Entry, string Folder)> Search(string term, PwGroup scope)
    {
        var results = new List<(int, PwEntry, string)>();
        var all = GetEntries(scope);

        for (int i = 0; i < all.Count; i++)
        {
            var e = all[i];
            bool match =
                e.Strings.ReadSafe(PwDefs.TitleField)   .Contains(term, StringComparison.OrdinalIgnoreCase) ||
                e.Strings.ReadSafe(PwDefs.UserNameField) .Contains(term, StringComparison.OrdinalIgnoreCase) ||
                e.Strings.GetKeys().Any(k => !StandardKeys.Contains(k) &&
                                             k.Contains(term, StringComparison.OrdinalIgnoreCase));
            if (match)
            {
                string folder = ReferenceEquals(e.ParentGroup, _db.RootGroup)
                    ? "" : (e.ParentGroup?.Name ?? "");
                results.Add((i + 1, e, folder));
            }
        }

        return results;
    }

    // ── Write: entries ────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a minimal entry with only a title and returns it.
    /// Useful for the quick-add flow where the user supplies extra fields later.
    /// </summary>
    public PwEntry AddQuickEntry(PwGroup targetGroup, string title)
    {
        var entry = new PwEntry(bCreateNewUuid: true, bSetTimes: true);
        entry.Strings.Set(PwDefs.TitleField, new ProtectedString(false, title));
        targetGroup.AddEntry(entry, bTakeOwnership: true);
        _modified = true;
        return entry;
    }

    /// <summary>Adds a new entry directly to <paramref name="targetGroup"/>.</summary>
    public void AddEntryTo(
        PwGroup targetGroup,
        string  title,
        string? username,
        string? password,
        string? url,
        string? notes,
        IReadOnlyList<(string Key, string Value)> customFields)
    {
        var entry = new PwEntry(bCreateNewUuid: true, bSetTimes: true);

        entry.Strings.Set(PwDefs.TitleField, new ProtectedString(false, title));
        if (!string.IsNullOrEmpty(username))
            entry.Strings.Set(PwDefs.UserNameField, new ProtectedString(false, username));
        if (!string.IsNullOrEmpty(password))
            entry.Strings.Set(PwDefs.PasswordField, new ProtectedString(true,  password));
        if (!string.IsNullOrEmpty(url))
            entry.Strings.Set(PwDefs.UrlField,      new ProtectedString(false, url));
        if (!string.IsNullOrEmpty(notes))
            entry.Strings.Set(PwDefs.NotesField,    new ProtectedString(false, notes));

        foreach (var (key, value) in customFields)
            entry.Strings.Set(key, new ProtectedString(true, value));

        targetGroup.AddEntry(entry, bTakeOwnership: true);
        _modified = true;
    }

    /// <summary>Updates a specific entry directly (for use when the reference is already known).</summary>
    public void UpdateEntry(
        PwEntry entry,
        string? title,
        string? username,
        string? password,
        string? url,
        string? notes)
    {
        entry.CreateBackup(_db);
        entry.Touch(bModified: true);

        if (title    is not null) entry.Strings.Set(PwDefs.TitleField,    new ProtectedString(false, title));
        if (username is not null) entry.Strings.Set(PwDefs.UserNameField, new ProtectedString(false, username));
        if (password is not null) entry.Strings.Set(PwDefs.PasswordField, new ProtectedString(true,  password));
        if (url      is not null) entry.Strings.Set(PwDefs.UrlField,      new ProtectedString(false, url));
        if (notes    is not null) entry.Strings.Set(PwDefs.NotesField,    new ProtectedString(false, notes));

        _modified = true;
    }

    /// <summary>
    /// Adds or overwrites a single field on <paramref name="entry"/>.
    /// Common key aliases (e.g. "pass", "user", "website") map to their standard
    /// KeePass field names. Unknown keys are stored as custom protected fields.
    /// A history snapshot is created before the change.
    /// </summary>
    public void SetEntryField(PwEntry entry, string key, string value)
    {
        entry.CreateBackup(_db);
        entry.Touch(bModified: true);

        // Resolve alias → canonical field name; fall back to the raw key (custom field).
        string fieldName = FieldAliases.TryGetValue(key, out string? mapped) ? mapped : key;

        // Passwords and all custom fields are stored as protected (in-memory encrypted).
        bool isProtected = fieldName == PwDefs.PasswordField || !StandardKeys.Contains(fieldName);

        entry.Strings.Set(fieldName, new ProtectedString(isProtected, value));
        _modified = true;
    }

    /// <summary>Updates the entry at zero-based <paramref name="index"/> within <paramref name="scope"/>.</summary>
    public bool UpdateEntry(
        PwGroup scope,
        int     index,
        string? title,
        string? username,
        string? password,
        string? url,
        string? notes)
    {
        var entries = GetEntries(scope);
        if (index < 0 || index >= entries.Count) return false;
        UpdateEntry(entries[index], title, username, password, url, notes);
        return true;
    }

    /// <summary>
    /// Removes a single field from <paramref name="entry"/>.
    /// Returns <c>false</c> if the field does not exist or resolves to the required Title field.
    /// </summary>
    public bool RemoveEntryField(PwEntry entry, string key)
    {
        string fieldName = FieldAliases.TryGetValue(key, out string? mapped) ? mapped : key;
        if (fieldName == PwDefs.TitleField) return false;
        if (entry.Strings.Get(fieldName) == null) return false;
        entry.CreateBackup(_db);
        entry.Touch(bModified: true);
        entry.Strings.Remove(fieldName);
        _modified = true;
        return true;
    }

    /// <summary>Resolves a field-name alias to its canonical KeePass field name.</summary>
    public string ResolveFieldAlias(string key)
        => FieldAliases.TryGetValue(key, out string? mapped) ? mapped : key;

    /// <summary>Permanently removes a specific entry (bypasses recycle bin).</summary>
    public void DeleteEntry(PwEntry entry)
    {
        entry.ParentGroup?.Entries.Remove(entry);
        _modified = true;
    }

    /// <summary>Permanently removes the entry at zero-based <paramref name="index"/> within <paramref name="scope"/>.</summary>
    public bool DeleteEntry(PwGroup scope, int index)
    {
        var entries = GetEntries(scope);
        if (index < 0 || index >= entries.Count) return false;
        DeleteEntry(entries[index]);
        return true;
    }

    /// <summary>
    /// Moves <paramref name="entry"/> to the Recycle Bin group, creating it if needed.
    /// </summary>
    public void MoveToRecycleBin(PwEntry entry)
    {
        var bin = GetOrCreateRecycleBin();
        entry.ParentGroup?.Entries.Remove(entry);
        bin.AddEntry(entry, bTakeOwnership: true);
        _modified = true;
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="entry"/>'s parent group is the Recycle Bin.
    /// </summary>
    public bool IsInRecycleBin(PwEntry entry)
    {
        if (entry.ParentGroup is null) return false;
        var binUuid = _db.RecycleBinUuid;
        if (binUuid is null || binUuid.Equals(PwUuid.Zero)) return false;
        return entry.ParentGroup.Uuid.Equals(binUuid);
    }

    /// <summary>
    /// Returns the Recycle Bin <see cref="PwGroup"/>, creating it under the root if absent.
    /// </summary>
    private PwGroup GetOrCreateRecycleBin()
    {
        // Try to find an existing recycle bin tracked by its UUID.
        var binUuid = _db.RecycleBinUuid;
        if (binUuid != null && !binUuid.Equals(PwUuid.Zero))
        {
            var existing = _db.RootGroup.FindGroup(binUuid, bSearchRecursive: true);
            if (existing != null) return existing;
        }

        // Create a new Recycle Bin group and register it in the database.
        var recycleBin = new PwGroup(bCreateNewUuid: true, bSetTimes: true,
            strName: "Recycle Bin", pwIcon: PwIcon.TrashBin);
        _db.RootGroup.AddGroup(recycleBin, bTakeOwnership: true);
        _db.RecycleBinUuid    = recycleBin.Uuid;
        _db.RecycleBinEnabled = true;
        _modified = true;
        return recycleBin;
    }

    /// <summary>Moves <paramref name="entry"/> to <paramref name="destination"/>.</summary>
    public void MoveEntryToGroup(PwEntry entry, PwGroup destination)
    {
        entry.ParentGroup?.Entries.Remove(entry);
        destination.AddEntry(entry, bTakeOwnership: true);
        _modified = true;
    }

    /// <summary>
    /// Moves <paramref name="group"/> under <paramref name="destination"/>.
    /// Call <see cref="WouldCreateCycle"/> first to guard against circular moves.
    /// </summary>
    public void MoveGroupToGroup(PwGroup group, PwGroup destination)
    {
        group.ParentGroup?.Groups.Remove(group);
        destination.AddGroup(group, bTakeOwnership: true);
        _modified = true;
    }

    /// <summary>
    /// Returns <c>true</c> if moving <paramref name="group"/> into <paramref name="newParent"/>
    /// would create a cycle (i.e. <paramref name="newParent"/> is the group itself or one of its descendants).
    /// </summary>
    public static bool WouldCreateCycle(PwGroup group, PwGroup newParent)
    {
        var cur = newParent;
        while (cur != null)
        {
            if (ReferenceEquals(cur, group)) return true;
            cur = cur.ParentGroup;
        }
        return false;
    }

    /// <summary>
    /// Searches the whole database for a group whose name matches <paramref name="name"/>
    /// (exact match first, then prefix, case-insensitive).
    /// </summary>
    public PwGroup? FindGroup(string name)
        => FindGroupRecursive(_db.RootGroup, name, exact: true)
        ?? FindGroupRecursive(_db.RootGroup, name, exact: false);

    private static PwGroup? FindGroupRecursive(PwGroup parent, string name, bool exact)
    {
        foreach (PwGroup g in parent.Groups)
        {
            bool match = exact
                ? g.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                : g.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase);
            if (match) return g;
            var found = FindGroupRecursive(g, name, exact);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Renames <paramref name="group"/> to <paramref name="newName"/>.</summary>
    public void RenameGroup(PwGroup group, string newName)
    {
        group.Name = newName;
        _modified = true;
    }

    /// <summary>Moves <paramref name="group"/> to the Recycle Bin, creating it if needed.</summary>
    public void MoveGroupToRecycleBin(PwGroup group)
    {
        var bin = GetOrCreateRecycleBin();
        group.ParentGroup?.Groups.Remove(group);
        bin.AddGroup(group, bTakeOwnership: true);
        _modified = true;
    }

    /// <summary>Permanently removes <paramref name="group"/> and all its contents.</summary>
    public void DeleteGroup(PwGroup group)
    {
        group.ParentGroup?.Groups.Remove(group);
        _modified = true;
    }

    /// <summary>Returns <c>true</c> when <paramref name="group"/>'s parent is the Recycle Bin.</summary>
    public bool IsGroupInRecycleBin(PwGroup group)
    {
        if (group.ParentGroup is null) return false;
        var binUuid = _db.RecycleBinUuid;
        if (binUuid is null || binUuid.Equals(PwUuid.Zero)) return false;
        return group.ParentGroup.Uuid.Equals(binUuid);
    }

    // ── Write: groups ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new folder inside <paramref name="parent"/>.
    /// Returns <c>false</c> if a folder with that name already exists.
    /// </summary>
    public bool CreateGroup(PwGroup parent, string name)
    {
        foreach (PwGroup g in parent.Groups)
            if (g.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return false;

        var newGroup = new PwGroup(bCreateNewUuid: true, bSetTimes: true, strName: name, pwIcon: PwIcon.Folder);
        parent.AddGroup(newGroup, bTakeOwnership: true);
        _modified = true;
        return true;
    }

    /// <summary>Returns the names of all groups in the database (depth-first).</summary>
    public IReadOnlyList<string> GetGroupNames()
    {
        var names = new List<string>();
        CollectGroupNames(_db.RootGroup, names);
        return names;
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    public MemoryStream? SaveToStream()
    {
        if (!_modified) return null;

        // KdbxFile.Save() closes the stream it writes to, so we capture the bytes
        // via ToArray() (which works even on a closed MemoryStream) and hand back a
        // fresh, readable stream positioned at 0.
        var temp = new MemoryStream();
        var kdbx = new KdbxFile(_db);
        kdbx.Save(temp, pgDataSource: null, KdbxFormat.Default, slLogger: null);
        return new MemoryStream(temp.ToArray());
    }

    public bool IsModified => _modified;

    /// <summary>Resets the modified flag after a successful upload.</summary>
    public void MarkSaved() => _modified = false;

    // ── Private helpers ───────────────────────────────────────────────────────

    private static void CollectEntries(PwGroup group, List<PwEntry> accumulator)
    {
        foreach (PwEntry entry in group.Entries)
            accumulator.Add(entry);
        foreach (PwGroup sub in group.Groups)
            CollectEntries(sub, accumulator);
    }

    private static void CollectGroupNames(PwGroup group, List<string> names)
    {
        foreach (PwGroup sub in group.Groups)
        {
            names.Add(sub.Name);
            CollectGroupNames(sub, names);
        }
    }

    public void Dispose() => _db.Close();
}
