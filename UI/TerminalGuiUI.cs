using KeePassLib;
using mykeepass.Parsing;
using mykeepass.Services;
using Terminal.Gui;

namespace mykeepass.UI;

/// <summary>
/// TUI implementation of <see cref="IUserInterface"/> built with Terminal.Gui v2.
/// </summary>
/// <remarks>
/// Layout (approximate):
/// <code>
/// ┌─ Folders ──┬─ History ──────────────────────────────────────────┐
/// │ TreeView   │ (scrollable read-only TextView)                    │
/// │  ▷ Root    │                                                    │
/// │  ...       │                                                    │
/// │            │                                                    │
/// ├─ Commands ─┤                                                    │
/// │ cmd ref    ├────────────────────────────────────────────────────┤
/// │            │  > _    (TextField — only focusable widget)        │
/// └────────────┴────────────────────────────────────────────────────┘
///   20% width        80% width
/// </code>
/// </remarks>
internal sealed class TerminalGuiUI : IUserInterface
{
    public Task RunAsync(
        KeePassService     keepass,
        GoogleDriveService drive,
        string             fileId,
        string             fileName)
    {
        Application.Init();
        try
        {
            BuildAndRun(keepass, drive, fileId, fileName);
        }
        finally
        {
            Application.Shutdown();
        }
        return Task.CompletedTask;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static void BuildAndRun(
        KeePassService     keepass,
        GoogleDriveService drive,
        string             fileId,
        string             fileName)
    {
        // ── History buffer ────────────────────────────────────────────────────
        var historyBuf = new System.Text.StringBuilder();
        TextView? historyView = null;

        void AppendHistory(string line)
        {
            historyBuf.AppendLine(line);
            if (historyView is null) return;
            historyView.Text = historyBuf.ToString();
            historyView.MoveEnd();
            historyView.SetNeedsDraw();
        }

        // ── Executor ──────────────────────────────────────────────────────────
        var interaction = new TerminalGuiInteraction(AppendHistory);
        var executor    = new CommandExecutor(keepass, drive, fileId, fileName, interaction);

        // ── Command history ───────────────────────────────────────────────────
        var cmdHistory = new List<string>();
        int histPos    = 0;

        // ── Partial-masking buffer ────────────────────────────────────────────
        // actualText   : the real command the user is typing (never masked).
        // inputField   : shows command prefix as-is, value portion as '•' chars.
        // Because the display and actual text always have the same LENGTH, the
        // TextField's cursor position maps 1-to-1 to actualText positions.
        var  actualText   = new System.Text.StringBuilder();
        bool fieldSyncing = false;   // re-entrancy guard for TextChanged

        // ── Black color scheme ────────────────────────────────────────────────
        var blackScheme = new ColorScheme
        {
            Normal    = new Terminal.Gui.Attribute(Color.White,       Color.Black),
            Focus     = new Terminal.Gui.Attribute(Color.White,       Color.Black),
            HotNormal = new Terminal.Gui.Attribute(Color.BrightGreen, Color.Black),
            HotFocus  = new Terminal.Gui.Attribute(Color.BrightGreen, Color.Black),
            Disabled  = new Terminal.Gui.Attribute(Color.Gray,        Color.Black),
        };

        // ── Window ────────────────────────────────────────────────────────────
        var window = new Window
        {
            Title       = $"MyKeePass — {fileName}",
            X           = 0,
            Y           = 0,
            Width       = Dim.Fill(),
            Height      = Dim.Fill(),
            ColorScheme = blackScheme,
        };

        // ── Left side: Folders (top, larger) ─────────────────────────────────
        var leftPanel = new FrameView
        {
            Title    = "Folders",
            X        = 0,
            Y        = 0,
            Width    = Dim.Percent(20),
            Height   = Dim.Percent(70),
            CanFocus = false,
        };

        var treeView = new TreeView<KeePassTreeNode>
        {
            X        = 0,
            Y        = 0,
            Width    = Dim.Fill(),
            Height   = Dim.Fill(),
            CanFocus = false,
        };
        leftPanel.Add(treeView);

        // ── Left side: Commands reference (bottom, smaller) ───────────────────
        var commandsFrame = new FrameView
        {
            Title    = "Commands",
            X        = 0,
            Y        = Pos.Bottom(leftPanel),
            Width    = Dim.Percent(20),
            Height   = Dim.Fill(),
            CanFocus = false,
        };
        var commandsView = new TextView
        {
            X        = 0,
            Y        = 0,
            Width    = Dim.Fill(),
            Height   = Dim.Fill(),
            ReadOnly = true,
            CanFocus = false,
        };
        commandsFrame.Add(commandsView);

        // ── Right side: History + input row ───────────────────────────────────
        // All right-side views are direct children of window (no intermediate
        // container) so focus traversal always finds inputField correctly.

        var historyFrame = new FrameView
        {
            Title    = "History",
            X        = Pos.Percent(20),
            Y        = 0,
            Width    = Dim.Fill(),
            Height   = Dim.Fill(1),   // fill minus 1 row for input
            CanFocus = false,
        };
        historyView = new TextView
        {
            X        = 0,
            Y        = 0,
            Width    = Dim.Fill(),
            Height   = Dim.Fill(),
            ReadOnly = true,
            CanFocus = false,
        };
        historyFrame.Add(historyView);

        // Input row — the ONLY focusable widget in the entire layout
        var inputPrompt = new Label
        {
            Text     = "> ",
            X        = Pos.Percent(20),
            Y        = Pos.Bottom(historyFrame),
            CanFocus = false,
        };
        var inputField = new TextField
        {
            X        = Pos.Right(inputPrompt),
            Y        = Pos.Bottom(historyFrame),
            Width    = Dim.Fill(),
            CanFocus = true,
        };

        window.Add(leftPanel, commandsFrame, historyFrame, inputPrompt, inputField);

        // ── Double-click on tree node → execute the equivalent select command ──
        treeView.ObjectActivated += async (_, e) =>
        {
            if (e.ActivatedObject is not { } node) return;

            string? commandText =
                node.IsGroup && node.Group is { } g
                    ? $"select folder {g.Name}"
                : node.IsEntry && node.Entry is { } en
                    ? $"select entry {en.Strings.ReadSafe(PwDefs.TitleField)}"
                : null;

            if (commandText is null) return;

            AppendHistory($"> {commandText}");
            try
            {
                await executor.ExecuteAsync(CommandParser.Parse(commandText));
            }
            catch (Exception ex)
            {
                AppendHistory($"[!] {ex.Message}");
            }

            Application.Invoke(() => inputField.SetFocus());
        };

        // ── Tree helpers ──────────────────────────────────────────────────────

        // Build the list [root, ..., target] or null if target not found.
        static List<PwGroup>? FindGroupPath(PwGroup current, PwGroup target)
        {
            if (current == target) return [current];
            foreach (var sub in current.Groups)
            {
                var sub_path = FindGroupPath(sub, target);
                if (sub_path is not null) { sub_path.Insert(0, current); return sub_path; }
            }
            return null;
        }

        // Generic depth-first search through the filtered builder.
        static KeePassTreeNode? FindNode(
            ITreeBuilder<KeePassTreeNode> builder,
            KeePassTreeNode               node,
            Func<KeePassTreeNode, bool>   predicate)
        {
            if (predicate(node)) return node;
            if (!builder.CanExpand(node)) return null;
            foreach (var child in builder.GetChildren(node))
            {
                var found = FindNode(builder, child, predicate);
                if (found is not null) return found;
            }
            return null;
        }

        void RefreshTree()
        {
            var targetGroup = executor.CurrentGroup;
            var path = FindGroupPath(keepass.RootGroup, targetGroup)
                       ?? [keepass.RootGroup];

            var builder = new PathFilteredTreeBuilder(path, executor.ViewedEntry);
            treeView.TreeBuilder = builder;

            treeView.ClearObjects();
            var rootNode = new KeePassTreeNode(keepass.RootGroup);
            treeView.AddObject(rootNode);
            treeView.ExpandAll();

            // Highlight the entry (when viewing one) or the current group.
            KeePassTreeNode? selectedNode = executor.ViewedEntry is not null
                ? FindNode(builder, rootNode, n => n.IsEntry && n.Entry == executor.ViewedEntry)
                : FindNode(builder, rootNode, n => n.IsGroup && n.Group == targetGroup);

            if (selectedNode is not null)
                treeView.SelectedObject = selectedNode;

            treeView.SetNeedsDraw();
        }

        void RefreshCommands()
        {
            commandsView.Text = executor.ViewedEntry is not null
                ? EntryViewHelp
                : FolderViewHelp;
            commandsView.SetNeedsDraw();
        }

        executor.StateChanged += () => Application.Invoke(() =>
        {
            RefreshTree();
            RefreshCommands();
        });

        RefreshTree();
        RefreshCommands();

        // ── Partial-masking helpers ───────────────────────────────────────────

        // Returns command prefix verbatim + '•' repeated for value portion.
        string GetDisplayText()
        {
            string s  = actualText.ToString();
            int    vs = ConsoleUI.ValueStartIndex(s);
            return vs < 0 ? s : s[..vs] + new string('•', s.Length - vs);
        }

        // Push the display text into the TextField without triggering our
        // TextChanged sync, then position the cursor.
        void SyncField(int newCursor = -1)
        {
            fieldSyncing = true;
            try
            {
                string d = GetDisplayText();
                inputField.Text           = d;
                inputField.CursorPosition = newCursor < 0
                    ? d.Length
                    : Math.Min(newCursor, d.Length);
            }
            finally { fieldSyncing = false; }
        }

        // Load a command string into the field (history navigation or clear).
        void LoadActual(string text)
        {
            actualText.Clear();
            actualText.Append(text);
            SyncField();
        }

        // ── History clear helper ──────────────────────────────────────────────
        void ClearHistory()
        {
            historyBuf.Clear();
            if (historyView is not null)
            {
                historyView.Text = "";
                historyView.SetNeedsDraw();
            }
        }

        // ── Input field key handling ──────────────────────────────────────────
        inputField.KeyDown += async (sender, key) =>
        {
            // ── Ctrl+L : clear history ────────────────────────────────────────
            if (key.KeyCode == (KeyCode.CtrlMask | KeyCode.L))
            {
                ClearHistory();
                key.Handled = true;
                return;
            }

            // ── Ctrl+V : paste ────────────────────────────────────────────────
            if (key.KeyCode == (KeyCode.CtrlMask | KeyCode.V))
            {
                if (Clipboard.TryGetClipboardData(out string clip) &&
                    !string.IsNullOrEmpty(clip))
                {
                    // Strip line breaks — common in copied API keys/passwords
                    clip = clip.Replace("\r\n", "").Replace("\r", "").Replace("\n", "");
                    int    pos     = inputField.CursorPosition;
                    string current = actualText.ToString();
                    actualText.Clear();
                    actualText.Append(current[..pos] + clip + current[pos..]);
                    SyncField(pos + clip.Length);
                }
                key.Handled = true;
                return;
            }

            // ── Up arrow : previous command ───────────────────────────────────
            if (key.KeyCode == KeyCode.CursorUp)
            {
                if (cmdHistory.Count > 0 && histPos > 0)
                    LoadActual(cmdHistory[--histPos]);
                key.Handled = true;
                return;
            }

            // ── Down arrow : next command ─────────────────────────────────────
            if (key.KeyCode == KeyCode.CursorDown)
            {
                if (histPos < cmdHistory.Count - 1)
                    LoadActual(cmdHistory[++histPos]);
                else
                {
                    histPos = cmdHistory.Count;
                    LoadActual("");
                }
                key.Handled = true;
                return;
            }

            // ── Enter : execute ───────────────────────────────────────────────
            if (key.KeyCode == KeyCode.Enter)
            {
                key.Handled = true;

                string text = actualText.ToString().Trim();
                LoadActual("");     // clear field

                if (string.IsNullOrEmpty(text)) return;

                if (text.Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    ClearHistory();
                    return;
                }

                // Store only the command prefix (never the secret value) so that
                // passwords are not kept in the in-memory history list.
                int    vs           = ConsoleUI.ValueStartIndex(text);
                string historyEntry = vs >= 0 ? text[..vs] : text;
                if (cmdHistory.Count == 0 || cmdHistory[^1] != historyEntry)
                    cmdHistory.Add(historyEntry);
                histPos = cmdHistory.Count;

                AppendHistory($"> {MaskForHistory(text)}");

                var command = CommandParser.Parse(text);
                try
                {
                    bool cont = await executor.ExecuteAsync(command);
                    if (!cont)
                        Application.Invoke(() => window.RequestStop());
                }
                catch (Exception ex)
                {
                    AppendHistory($"[!] Unexpected error: {ex.Message}");
                }
                return;
            }

            // ── Backspace : delete char before cursor ─────────────────────────
            if (key.KeyCode == KeyCode.Backspace)
            {
                int pos = inputField.CursorPosition;
                if (pos > 0 && ConsoleUI.ValueStartIndex(actualText.ToString()) >= 0)
                {
                    // In value mode — handle manually so TextField never sees
                    // unmasked chars.
                    string s = actualText.ToString();
                    actualText.Clear();
                    actualText.Append(s[..(pos - 1)] + s[pos..]);
                    SyncField(pos - 1);
                    key.Handled = true;
                }
                return;  // else let TextField handle (non-value mode)
            }

            // ── Delete : delete char at cursor ────────────────────────────────
            if (key.KeyCode == KeyCode.Delete)
            {
                int pos = inputField.CursorPosition;
                string s = actualText.ToString();
                if (pos < s.Length && ConsoleUI.ValueStartIndex(s) >= 0)
                {
                    actualText.Clear();
                    actualText.Append(s[..pos] + s[(pos + 1)..]);
                    SyncField(pos);
                    key.Handled = true;
                }
                return;  // else let TextField handle (non-value mode)
            }

            // ── Printable character ───────────────────────────────────────────
            if (key.AsRune.Value > 0)
            {
                int    pos         = inputField.CursorPosition;
                string current     = actualText.ToString();
                string rune        = key.AsRune.ToString();
                string hypothetical = current[..pos] + rune + current[pos..];

                if (ConsoleUI.ValueStartIndex(hypothetical) >= 0)
                {
                    // Would enter (or is in) value mode — handle manually so
                    // the actual character is never visible in the TextField.
                    actualText.Clear();
                    actualText.Append(hypothetical);
                    SyncField(pos + rune.Length);
                    key.Handled = true;
                }
                // Non-value mode: fall through — let TextField handle it.
                // TextChanged will sync actualText.
            }
        };

        // ── Non-value-mode sync ───────────────────────────────────────────────
        // When the user edits in non-value mode (Terminal.Gui handles the key),
        // keep actualText in sync with what's displayed.
        inputField.TextChanged += (_, _) =>
        {
            if (fieldSyncing) return;
            string displayed = inputField.Text ?? "";
            // Only sync when the displayed text has no value portion
            // (i.e., we're in pure command-prefix mode).
            if (ConsoleUI.ValueStartIndex(displayed) < 0)
            {
                actualText.Clear();
                actualText.Append(displayed);
            }
        };

        // ── Force focus to input field on first loop iteration ────────────────
        // Registered before Application.Run so it fires at the very start of
        // the event loop, after all layout/init is complete.
        Application.AddTimeout(TimeSpan.Zero, () =>
        {
            inputField.SetFocus();
            return false; // run once only
        });

        Application.Run(window);
        window.Dispose();
    }

    /// <summary>
    /// Replaces the secret value portion of a command with *** for safe display in history.
    /// </summary>
    private static string MaskForHistory(string command)
    {
        int valueStart = ConsoleUI.ValueStartIndex(command);
        if (valueStart < 0) return command;
        return command[..valueStart] + "***";
    }

    // ── Help text ─────────────────────────────────────────────────────────────

    private const string FolderViewHelp =
        "  add / create <name>              Create a new entry\n" +
        "  add / create folder <name>       Create a subfolder\n" +
        "  update / modify <name>           Edit an entry\n" +
        "  delete / remove <name>           Move entry to recycle bin\n" +
        "  delete / remove folder <name>    Move subfolder to recycle bin\n" +
        "  rename <name> to <new>           Rename an entry or folder\n" +
        "  move <name> to <folder>          Move an entry or folder\n" +
        "  search <term>                    Search entries\n" +
        "  copy <field> from <name>         Copy field to clipboard (60s)\n" +
        "  select <name>                    Open folder or entry (auto)\n" +
        "  select folder / entry <name>     Navigate explicitly\n" +
        "  list                             List folder contents\n" +
        "  save                             Upload to Google Drive\n" +
        "  back                             Go to parent folder\n" +
        "  exit                             Exit";

    private const string EntryViewHelp =
        "  set <field> to <val>             Set a field value (masked)\n" +
        "  modify <field> to <val>          Alias for set\n" +
        "  add [key] <field> with value <v> Set a field value (masked)\n" +
        "  add [key] <field> = <val>        Shorthand (masked)\n" +
        "  delete <field>                   Remove a custom field\n" +
        "  rename to <new-title>            Rename this entry\n" +
        "  move to <folder>                 Move this entry\n" +
        "  copy <field>                     Copy field to clipboard (60s)\n" +
        "  update                           Edit all fields interactively\n" +
        "  delete / remove                  Move entry to recycle bin\n" +
        "  save                             Upload to Google Drive\n" +
        "  back                             Return to folder\n" +
        "  exit                             Exit";
}
