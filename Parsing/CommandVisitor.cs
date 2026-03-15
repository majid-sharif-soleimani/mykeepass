using System.Linq;

namespace mykeepass.Parsing;

/// <summary>
/// Converts an ANTLR4 parse tree into a strongly-typed <see cref="ICommand"/>.
/// </summary>
internal sealed class CommandVisitor : MyKeePassParserBaseVisitor<ICommand>
{
    // ── Top-level command dispatch ────────────────────────────────────────────

    public override ICommand VisitSetFieldCmd(MyKeePassParser.SetFieldCmdContext ctx)
        => Visit(ctx.setFieldCommand());

    public override ICommand VisitSelectCmd(MyKeePassParser.SelectCmdContext ctx)
        => Visit(ctx.selectCommand());

    public override ICommand VisitAddFolderCmd(MyKeePassParser.AddFolderCmdContext ctx)
        => Visit(ctx.addFolderCommand());

    public override ICommand VisitAddEntryCmd(MyKeePassParser.AddEntryCmdContext ctx)
        => Visit(ctx.addEntryCommand());

    public override ICommand VisitDeleteCmd(MyKeePassParser.DeleteCmdContext ctx)
        => Visit(ctx.deleteCommand());

    public override ICommand VisitEditCmd(MyKeePassParser.EditCmdContext ctx)
        => Visit(ctx.editCommand());

    public override ICommand VisitSearchCmd(MyKeePassParser.SearchCmdContext ctx)
        => Visit(ctx.searchCommand());

    public override ICommand VisitCopyCmd(MyKeePassParser.CopyCmdContext ctx)
        => Visit(ctx.copyCommand());

    public override ICommand VisitListCmd(MyKeePassParser.ListCmdContext ctx)
        => Visit(ctx.listCommand());

    public override ICommand VisitSaveCmd(MyKeePassParser.SaveCmdContext ctx)
        => Visit(ctx.saveCommand());

    public override ICommand VisitBackCmd(MyKeePassParser.BackCmdContext ctx)
        => Visit(ctx.backCommand());

    public override ICommand VisitExitCmd(MyKeePassParser.ExitCmdContext ctx)
        => Visit(ctx.exitCommand());

    public override ICommand VisitEmptyCmd(MyKeePassParser.EmptyCmdContext ctx)
        => new EmptyCommand();

    // ── setFieldCommand — "with value" forms ─────────────────────────────────

    // add/update key <field> with value <val>  (explicit "key" hint word)
    public override ICommand VisitSetFieldKeyedWithValue(MyKeePassParser.SetFieldKeyedWithValueContext ctx)
        => SetOrAttachWithValue(GetFieldName(ctx.fieldName()), ValueOf(ctx.valueText()));

    // add/update <field> with value <val>
    public override ICommand VisitSetFieldWithValue(MyKeePassParser.SetFieldWithValueContext ctx)
        => SetOrAttachWithValue(GetFieldName(ctx.fieldName()), ValueOf(ctx.valueText()));

    // ── setFieldCommand — "= val" forms ──────────────────────────────────────

    // add/update key <field> = <val>  (explicit "key" hint word)
    public override ICommand VisitSetFieldKeyedWithEq(MyKeePassParser.SetFieldKeyedWithEqContext ctx)
        => new SetFieldCommand(GetFieldName(ctx.fieldName()), ValueOf(ctx.valueText()));

    // add/update <field> = <val>
    public override ICommand VisitSetFieldWithEq(MyKeePassParser.SetFieldWithEqContext ctx)
        => new SetFieldCommand(GetFieldName(ctx.fieldName()), ValueOf(ctx.valueText()));

    // ── setFieldCommand — "modify" forms ─────────────────────────────────────

    // modify key <field> to <val>  (explicit "key" hint word)
    public override ICommand VisitModifyFieldKeyed(MyKeePassParser.ModifyFieldKeyedContext ctx)
        => SetOrAttachViaTo(GetFieldName(ctx.fieldName()), ValueOf(ctx.valueText()));

    // modify <field> to <val>
    public override ICommand VisitModifyFieldDirect(MyKeePassParser.ModifyFieldDirectContext ctx)
        => SetOrAttachViaTo(GetFieldName(ctx.fieldName()), ValueOf(ctx.valueText()));

    // ── setFieldCommand — "set" forms ─────────────────────────────────────────

    // set key <field> to <val>  (explicit "key" hint word)
    public override ICommand VisitSetFieldKeyedCmd(MyKeePassParser.SetFieldKeyedCmdContext ctx)
        => SetOrAttachViaTo(GetFieldName(ctx.fieldName()), ValueOf(ctx.valueText()));

    // set <field> to <val>
    public override ICommand VisitSetFieldDirectCmd(MyKeePassParser.SetFieldDirectCmdContext ctx)
        => SetOrAttachViaTo(GetFieldName(ctx.fieldName()), ValueOf(ctx.valueText()));

    // ── selectCommand ─────────────────────────────────────────────────────────

    public override ICommand VisitSelectFolder(MyKeePassParser.SelectFolderContext ctx)
        => new SelectCommand(GetName(ctx.name()), SelectTarget.Folder);

    public override ICommand VisitSelectEntry(MyKeePassParser.SelectEntryContext ctx)
        => new SelectCommand(GetName(ctx.name()), SelectTarget.Entry);

    public override ICommand VisitSelectAuto(MyKeePassParser.SelectAutoContext ctx)
        => new SelectCommand(GetName(ctx.name()), SelectTarget.Auto);

    // ── addFolderCommand ──────────────────────────────────────────────────────

    public override ICommand VisitAddFolderWithName(MyKeePassParser.AddFolderWithNameContext ctx)
        => new AddFolderCommand(GetName(ctx.name()));

    public override ICommand VisitAddFolderPrompt(MyKeePassParser.AddFolderPromptContext ctx)
        => new AddFolderCommand(null);

    public override ICommand VisitAddFolderBareWithName(MyKeePassParser.AddFolderBareWithNameContext ctx)
        => new AddFolderCommand(GetName(ctx.name()));

    public override ICommand VisitAddFolderBarePrompt(MyKeePassParser.AddFolderBarePromptContext ctx)
        => new AddFolderCommand(null);

    // ── addEntryCommand ───────────────────────────────────────────────────────

    public override ICommand VisitAddEntryExplicit(MyKeePassParser.AddEntryExplicitContext ctx)
        => new AddEntryCommand(GetName(ctx.name()));

    public override ICommand VisitAddEntryShorthand(MyKeePassParser.AddEntryShorthandContext ctx)
        => new AddEntryCommand(GetName(ctx.name()));

    public override ICommand VisitAddEntryInteractive(MyKeePassParser.AddEntryInteractiveContext ctx)
        => new AddEntryCommand(null);

    // ── deleteCommand ─────────────────────────────────────────────────────────

    public override ICommand VisitDeleteFolderByName(MyKeePassParser.DeleteFolderByNameContext ctx)
        => new DeleteFolderCommand(GetName(ctx.name()));

    public override ICommand VisitDeleteByName(MyKeePassParser.DeleteByNameContext ctx)
        => new DeleteCommand(GetName(ctx.name()));

    public override ICommand VisitDeleteInteractive(MyKeePassParser.DeleteInteractiveContext ctx)
        => new DeleteCommand(null);

    // ── moveCommand ───────────────────────────────────────────────────────────

    public override ICommand VisitMoveCmd(MyKeePassParser.MoveCmdContext ctx)
        => Visit(ctx.moveCommand());

    public override ICommand VisitMoveFolderCmd(MyKeePassParser.MoveFolderCmdContext ctx)
        => new MoveCommand(GetName(ctx.name()), ValueOf(ctx.valueText()), IsFolder: true);

    public override ICommand VisitMoveEntryCmd(MyKeePassParser.MoveEntryCmdContext ctx)
        => new MoveCommand(GetName(ctx.name()), ValueOf(ctx.valueText()), IsFolder: false);

    public override ICommand VisitMoveCurrentCmd(MyKeePassParser.MoveCurrentCmdContext ctx)
        => new MoveCommand(null, ValueOf(ctx.valueText()), IsFolder: false);

    // ── renameCommand ─────────────────────────────────────────────────────────

    public override ICommand VisitRenameCmd(MyKeePassParser.RenameCmdContext ctx)
        => Visit(ctx.renameCommand());

    public override ICommand VisitRenameFolderCmd(MyKeePassParser.RenameFolderCmdContext ctx)
        => new RenameCommand(GetName(ctx.name()), ValueOf(ctx.valueText()), IsFolder: true);

    public override ICommand VisitRenameByNameCmd(MyKeePassParser.RenameByNameCmdContext ctx)
        => new RenameCommand(GetName(ctx.name()), ValueOf(ctx.valueText()), IsFolder: false);

    public override ICommand VisitRenameCurrentCmd(MyKeePassParser.RenameCurrentCmdContext ctx)
        => new RenameCommand(null, ValueOf(ctx.valueText()), IsFolder: false);

    // ── editCommand ───────────────────────────────────────────────────────────

    public override ICommand VisitEditByName(MyKeePassParser.EditByNameContext ctx)
        => new EditCommand(GetName(ctx.name()));

    public override ICommand VisitEditInteractive(MyKeePassParser.EditInteractiveContext ctx)
        => new EditCommand(null);

    // ── searchCommand ─────────────────────────────────────────────────────────

    public override ICommand VisitSearchWithTerm(MyKeePassParser.SearchWithTermContext ctx)
        => new SearchCommand(GetName(ctx.name()));

    public override ICommand VisitSearchInteractive(MyKeePassParser.SearchInteractiveContext ctx)
        => new SearchCommand(null);

    // ── copyCommand ───────────────────────────────────────────────────────────

    public override ICommand VisitCopyFrom(MyKeePassParser.CopyFromContext ctx)
        => new CopyCommand(GetFieldRef(ctx.fieldRef()), GetName(ctx.name()));

    public override ICommand VisitCopyField(MyKeePassParser.CopyFieldContext ctx)
        => new CopyCommand(GetFieldRef(ctx.fieldRef()), null);

    public override ICommand VisitCopyInteractive(MyKeePassParser.CopyInteractiveContext ctx)
        => new CopyCommand(null, null);

    // ── listCommand / saveCommand / backCommand / exitCommand ────────────────

    public override ICommand VisitListCommand(MyKeePassParser.ListCommandContext ctx)
        => new ListCommand();

    public override ICommand VisitSaveCommand(MyKeePassParser.SaveCommandContext ctx)
        => new SaveCommand();

    public override ICommand VisitBackCommand(MyKeePassParser.BackCommandContext ctx)
        => new BackCommand();

    public override ICommand VisitExitCommand(MyKeePassParser.ExitCommandContext ctx)
        => new ExitCommand();

    // ── Fallback ──────────────────────────────────────────────────────────────

    protected override ICommand DefaultResult => new UnknownCommand(string.Empty);

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the field name from a <c>fieldName</c> context, stripping
    /// surrounding double quotes when a STRING token is present.
    /// </summary>
    private static string GetFieldName(MyKeePassParser.FieldNameContext ctx)
    {
        if (ctx.STRING() != null)
        {
            string text = ctx.STRING().GetText();   // includes surrounding quotes
            return text[1..^1];                     // strip first and last char
        }
        return ctx.WORD().GetText();
    }

    /// <summary>
    /// Like <see cref="GetFieldName"/> but for the <c>fieldRef</c> rule (copy
    /// commands), which allows keyword tokens via <c>anyWord</c> in addition to
    /// STRING.
    /// </summary>
    private static string GetFieldRef(MyKeePassParser.FieldRefContext ctx)
    {
        if (ctx.STRING() != null)
        {
            string text = ctx.STRING().GetText();
            return text[1..^1];
        }
        return ctx.anyWord().GetText();
    }

    /// <summary>
    /// Extracts the field value from a <c>valueText</c> context, trimming the
    /// single leading space that VALUE_MODE always captures after the separator.
    /// </summary>
    private static string ValueOf(MyKeePassParser.ValueTextContext ctx)
        => ctx.VALUE_TEXT().GetText().TrimStart(' ');

    /// <summary>
    /// For "add/update … with value …" forms: returns <see cref="AttachFileCommand"/>
    /// when <paramref name="value"/> starts with "from " (case-insensitive), otherwise
    /// returns <see cref="SetFieldCommand"/>.
    /// </summary>
    private static ICommand SetOrAttachWithValue(string key, string value)
    {
        const string prefix = "from ";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return new AttachFileCommand(key, value[prefix.Length..]);
        return new SetFieldCommand(key, value);
    }

    /// <summary>
    /// For "set/modify … to …" forms: returns <see cref="AttachFileCommand"/>
    /// when <paramref name="value"/> starts with "value from " (case-insensitive),
    /// otherwise returns <see cref="SetFieldCommand"/>.
    /// </summary>
    private static ICommand SetOrAttachViaTo(string key, string value)
    {
        const string prefix = "value from ";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return new AttachFileCommand(key, value[prefix.Length..]);
        return new SetFieldCommand(key, value);
    }

    /// <summary>
    /// Joins all <c>anyWord</c> children of a <c>name</c> context with a single
    /// space, preserving original casing (e.g. "Amazon Prime", "My Gmail").
    /// </summary>
    private static string GetName(MyKeePassParser.NameContext ctx)
        => string.Join(" ", ctx.anyWord().Select(w => w.GetText()));
}
