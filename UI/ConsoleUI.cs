using KeePassLib;
using mykeepass.Parsing;
using mykeepass.Services;

namespace mykeepass.UI;

/// <summary>
/// Console-based implementation of <see cref="IUserInterface"/>.
/// Owns the REPL loop, breadcrumb rendering, command-history, and value masking
/// exactly as the original <c>RunInteractiveLoopAsync</c> in Program.cs did.
/// </summary>
internal sealed class ConsoleUI : IUserInterface
{
    public async Task RunAsync(
        KeePassService     keepass,
        GoogleDriveService drive,
        string             fileId,
        string             fileName)
    {
        var interaction = new ConsoleInteraction();
        var executor    = new CommandExecutor(keepass, drive, fileId, fileName, interaction);

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
                CommandExecutor.ShowEntry(keepass, executor.ViewedEntry, interaction);
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

        // ── Main loop ─────────────────────────────────────────────────────────
        while (true)
        {
            PrintMenu();
            ICommand command = CommandParser.Parse(ReadChoice(history, PrintMenu));
            if (!await executor.ExecuteAsync(command)) return;
        }
    }

    // ── Console input helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Returns the buffer index where the secret value starts (masking begins),
    /// or -1 if the current input does not have a maskable value yet.
    /// </summary>
    internal static int ValueStartIndex(string buf)
    {
        string lo = buf.ToLowerInvariant();

        if (lo.StartsWith("add ")    || lo.StartsWith("create ") ||
            lo.StartsWith("insert ") || lo.StartsWith("update "))
        {
            int wvIdx = lo.IndexOf(" with value ");
            if (wvIdx >= 0)
            {
                int start = wvIdx + " with value ".Length;
                if (buf.Length <= start) return -1;
                if (lo[start..].StartsWith("from ")) return -1;
                return start;
            }

            int eqIdx = lo.IndexOf(" = ");
            if (eqIdx >= 0)
            {
                int start = eqIdx + " = ".Length;
                return buf.Length > start ? start : -1;
            }
        }

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

    /// <summary>
    /// Reads a full line preserving original case, with history navigation,
    /// cursor movement, Ctrl+L redraw, and value masking.
    /// </summary>
    private static string ReadChoice(List<string> history, Action printMenu)
    {
        var    sb         = new System.Text.StringBuilder();
        int    cursorPos  = 0;
        int    histPos    = history.Count;
        string savedInput = "";

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

        void ReplaceText(string newText)
        {
            if (cursorPos > 0) Console.Write(new string('\b', cursorPos));
            if (sb.Length > 0)
            {
                Console.Write(new string(' ',  sb.Length));
                Console.Write(new string('\b', sb.Length));
            }
            sb.Clear();
            sb.Append(newText);
            cursorPos = newText.Length;

            int vs = ValueStartIndex(newText);
            if (vs >= 0)
            {
                Console.Write(newText[..vs]);
                Console.Write(new string('*', newText.Length - vs));
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
                printMenu();
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
                    savedInput = sb.ToString();
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
                if (!string.IsNullOrEmpty(result))
                {
                    // Store only the command prefix — never the secret value — so
                    // plaintext passwords are not kept in the in-memory history list.
                    int    vs           = ValueStartIndex(result);
                    string historyEntry = vs >= 0 ? result[..vs] : result;
                    if (history.Count == 0 || history[^1] != historyEntry)
                        history.Add(historyEntry);
                }
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
                        Console.Write('\b');
                        RenderTail(cursorPos, vsAfterBs);
                        Console.Write(' ');
                        Console.Write(new string('\b', sb.Length - cursorPos + 1));
                    }
                    else
                    {
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
                    Console.Write(k.KeyChar);
                    RenderTail(cursorPos, -1);
                    int moveBack = sb.Length - cursorPos;
                    if (moveBack > 0) Console.Write(new string('\b', moveBack));
                }
                else if (vsBefore >= 0 && vsAfter >= 0)
                {
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
}
