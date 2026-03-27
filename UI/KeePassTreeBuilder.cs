using KeePassLib;
using mykeepass.Helpers;
using Terminal.Gui;

namespace mykeepass.UI;

/// <summary>
/// Represents one node in the TUI folder tree: a group, an entry, or an
/// attribute (key/value field of a viewed entry).
/// </summary>
public sealed class KeePassTreeNode
{
    public PwGroup? Group          { get; }
    public PwEntry? Entry          { get; }
    public string?  AttributeKey   { get; }   // non-null only for attribute nodes
    public string?  AttributeValue { get; }   // already masked if protected

    public bool IsGroup     => Group is not null;
    public bool IsEntry     => Entry is not null;
    public bool IsAttribute => AttributeKey is not null;

    public KeePassTreeNode(PwGroup group) { Group = group; }
    public KeePassTreeNode(PwEntry entry) { Entry = entry; }
    public KeePassTreeNode(string key, string displayValue)
    {
        AttributeKey   = key;
        AttributeValue = displayValue;
    }

    public override string ToString()
    {
        if (Group is not null)
            return $"▷ {ConsoleHelper.RtlDisplay(Group.Name)}";

        if (Entry is not null)
            return $"◇ {ConsoleHelper.RtlDisplay(Entry.Strings.ReadSafe(PwDefs.TitleField))}";

        if (AttributeKey is not null)
        {
            string val = AttributeValue ?? "";
            if (val.Length > 40) val = val[..40] + "…";
            return $"  {ConsoleHelper.RtlDisplay(AttributeKey)}: {val}";
        }

        return "?";
    }
}

/// <summary>
/// Full tree builder — expands every group to show all its entries and
/// sub-groups.  Used as a helper for finding group paths.
/// </summary>
public sealed class KeePassTreeBuilder : ITreeBuilder<KeePassTreeNode>
{
    public bool SupportsCanExpand => true;

    public bool CanExpand(KeePassTreeNode node)
        => node.IsGroup &&
           (node.Group!.Groups.UCount > 0 || node.Group!.Entries.UCount > 0);

    public IEnumerable<KeePassTreeNode> GetChildren(KeePassTreeNode node)
    {
        if (node.Group is null) yield break;

        foreach (PwEntry entry in node.Group.Entries)
            yield return new KeePassTreeNode(entry);

        foreach (PwGroup sub in node.Group.Groups)
            yield return new KeePassTreeNode(sub);
    }
}

/// <summary>
/// Filtered tree builder that shows only the ancestor path to the current
/// group/entry, plus the immediate children of the deepest selected node.
///
/// Layout when a folder is selected:
///   Root ▷ … ▷ CurrentGroup ▷ [entries + sub-groups of CurrentGroup]
///
/// Layout when an entry is selected:
///   Root ▷ … ▷ ParentGroup ▷ SelectedEntry ▷ [attribute nodes]
/// </summary>
public sealed class PathFilteredTreeBuilder : ITreeBuilder<KeePassTreeNode>
{
    // Groups from root down to the current group (inclusive on both ends).
    private readonly List<PwGroup> _path;
    private readonly PwEntry?      _selectedEntry;

    public PathFilteredTreeBuilder(List<PwGroup> path, PwEntry? selectedEntry)
    {
        _path          = path;
        _selectedEntry = selectedEntry;
    }

    public bool SupportsCanExpand => true;

    public bool CanExpand(KeePassTreeNode node)
    {
        if (node.IsGroup) return _path.Contains(node.Group!);
        if (node.IsEntry) return node.Entry == _selectedEntry;
        return false; // attribute nodes are leaves
    }

    public IEnumerable<KeePassTreeNode> GetChildren(KeePassTreeNode node)
    {
        // ── Group node ────────────────────────────────────────────────────────
        if (node.IsGroup)
        {
            var group = node.Group!;
            int idx   = _path.IndexOf(group);
            if (idx < 0) yield break;

            if (idx < _path.Count - 1)
            {
                // Intermediate ancestor — yield only the next group in path
                // (hides all siblings at this level)
                yield return new KeePassTreeNode(_path[idx + 1]);
            }
            else
            {
                // Deepest group (current group) — show its contents
                if (_selectedEntry is not null)
                {
                    // Only the selected entry is shown at this level
                    yield return new KeePassTreeNode(_selectedEntry);
                }
                else
                {
                    foreach (var e in group.Entries)
                        yield return new KeePassTreeNode(e);
                    foreach (var g in group.Groups)
                        yield return new KeePassTreeNode(g);
                }
            }
        }

        // ── Entry node (only the selected entry is expandable) ────────────────
        else if (node.IsEntry && node.Entry == _selectedEntry)
        {
            foreach (var s in _selectedEntry.Strings)
            {
                string display = s.Value.IsProtected ? "***" : s.Value.ReadString();
                yield return new KeePassTreeNode(s.Key, display);
            }
        }
    }
}
