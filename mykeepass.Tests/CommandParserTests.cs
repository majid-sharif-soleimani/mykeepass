using mykeepass.Parsing;
using Xunit;

namespace mykeepass.Tests;

/// <summary>
/// Exercises every command syntax accepted by the ANTLR grammar via
/// <see cref="CommandParser.Parse"/>.  No KeePass database is required.
/// </summary>
public class CommandParserTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static T Parse<T>(string input) where T : ICommand
    {
        var cmd = CommandParser.Parse(input);
        Assert.IsType<T>(cmd);
        return (T)cmd;
    }

    private static void AssertUnknown(string input)
        => Assert.IsType<UnknownCommand>(CommandParser.Parse(input));

    private static void AssertEmpty(string input)
        => Assert.IsType<EmptyCommand>(CommandParser.Parse(input));

    // ═══════════════════════════════════════════════════════════════════════
    // 1.  Empty / unknown
    // ═══════════════════════════════════════════════════════════════════════

    [Fact] public void Empty_input_produces_EmptyCommand()
        => AssertEmpty("");

    [Fact] public void Whitespace_only_produces_EmptyCommand()
        => AssertEmpty("   ");

    [Fact] public void Gibberish_produces_UnknownCommand()
        => AssertUnknown("foobar");

    [Fact] public void Unknown_command_preserves_original_input()
    {
        var cmd = (UnknownCommand)CommandParser.Parse("zzz unknown");
        Assert.Equal("zzz unknown", cmd.Input);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 2.  exit / quit / q
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("exit")]
    [InlineData("EXIT")]
    [InlineData("quit")]
    [InlineData("QUIT")]
    [InlineData("q")]
    [InlineData("Q")]
    public void Exit_variants(string input) => Parse<ExitCommand>(input);

    // ═══════════════════════════════════════════════════════════════════════
    // 3.  back
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("back")]
    [InlineData("BACK")]
    public void Back_command(string input) => Parse<BackCommand>(input);

    // ═══════════════════════════════════════════════════════════════════════
    // 4.  list / ls
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("list")]
    [InlineData("LIST")]
    [InlineData("ls")]
    [InlineData("LS")]
    public void List_command(string input) => Parse<ListCommand>(input);

    // ═══════════════════════════════════════════════════════════════════════
    // 5.  SetFieldCommand — "with value" form
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("add password with value S3cr3t",         "password", "S3cr3t")]
    [InlineData("ADD PASSWORD WITH VALUE S3cr3t",         "PASSWORD", "S3cr3t")]
    [InlineData("create title with value My Entry",       "title",    "My Entry")]
    [InlineData("insert url with value https://x.com",   "url",      "https://x.com")]
    [InlineData("update notes with value some note here","notes",    "some note here")]
    // preserve internal spaces in value
    [InlineData("add password with value hello world!",  "password", "hello world!")]
    // preserve special characters
    [InlineData("add password with value p@ss w0rd!#$",  "password", "p@ss w0rd!#$")]
    public void SetField_with_value_form(string input, string key, string value)
    {
        var cmd = Parse<SetFieldCommand>(input);
        Assert.Equal(key,   cmd.Key,   ignoreCase: true);
        Assert.Equal(value, cmd.Value);
    }

    [Theory]
    // explicit "key" hint word — second word is the field name
    [InlineData("add key password with value S3cr3t",    "password", "S3cr3t")]
    [InlineData("update key title with value New Title", "title",    "New Title")]
    [InlineData("create key notes with value memo",      "notes",    "memo")]
    public void SetField_keyed_with_value_form(string input, string key, string value)
    {
        var cmd = Parse<SetFieldCommand>(input);
        Assert.Equal(key,   cmd.Key,   ignoreCase: true);
        Assert.Equal(value, cmd.Value);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 6.  SetFieldCommand — "= val" form
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("add password = S3cr3t",         "password", "S3cr3t")]
    [InlineData("update title = My New Title",   "title",    "My New Title")]
    [InlineData("create url = https://x.com",    "url",      "https://x.com")]
    // preserve spaces after "="
    [InlineData("add password = p@ss w0rd",      "password", "p@ss w0rd")]
    public void SetField_eq_form(string input, string key, string value)
    {
        var cmd = Parse<SetFieldCommand>(input);
        Assert.Equal(key,   cmd.Key,   ignoreCase: true);
        Assert.Equal(value, cmd.Value);
    }

    [Theory]
    // explicit "key" hint word with equals
    [InlineData("add key password = S3cr3t",     "password", "S3cr3t")]
    [InlineData("update key title = New Title",  "title",    "New Title")]
    public void SetField_keyed_eq_form(string input, string key, string value)
    {
        var cmd = Parse<SetFieldCommand>(input);
        Assert.Equal(key,   cmd.Key,   ignoreCase: true);
        Assert.Equal(value, cmd.Value);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 7.  SetFieldCommand — "modify … to" form
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("modify password to S3cr3t",           "password", "S3cr3t")]
    [InlineData("MODIFY TITLE TO My Entry",            "TITLE",    "My Entry")]
    [InlineData("modify notes to multi word note",     "notes",    "multi word note")]
    // value with special chars
    [InlineData("modify password to p@ss!#$%",         "password", "p@ss!#$%")]
    public void SetField_modify_direct(string input, string key, string value)
    {
        var cmd = Parse<SetFieldCommand>(input);
        Assert.Equal(key,   cmd.Key,   ignoreCase: true);
        Assert.Equal(value, cmd.Value);
    }

    [Theory]
    // explicit "key" hint word
    [InlineData("modify key password to S3cr3t",   "password", "S3cr3t")]
    [InlineData("modify key title to My Entry",    "title",    "My Entry")]
    public void SetField_modify_keyed(string input, string key, string value)
    {
        var cmd = Parse<SetFieldCommand>(input);
        Assert.Equal(key,   cmd.Key,   ignoreCase: true);
        Assert.Equal(value, cmd.Value);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 8.  SetFieldCommand — "set … to" form
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("set password to S3cr3t",           "password", "S3cr3t")]
    [InlineData("SET TITLE TO My Entry",            "TITLE",    "My Entry")]
    [InlineData("set notes to multi word note",     "notes",    "multi word note")]
    [InlineData("set url to https://example.com",   "url",      "https://example.com")]
    public void SetField_set_direct(string input, string key, string value)
    {
        var cmd = Parse<SetFieldCommand>(input);
        Assert.Equal(key,   cmd.Key,   ignoreCase: true);
        Assert.Equal(value, cmd.Value);
    }

    [Theory]
    [InlineData("set key password to S3cr3t",   "password", "S3cr3t")]
    [InlineData("set key title to My Entry",    "title",    "My Entry")]
    public void SetField_set_keyed(string input, string key, string value)
    {
        var cmd = Parse<SetFieldCommand>(input);
        Assert.Equal(key,   cmd.Key,   ignoreCase: true);
        Assert.Equal(value, cmd.Value);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 9.  select
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("select gmail",           "gmail",           SelectTarget.Auto)]
    [InlineData("select Amazon Prime",    "Amazon Prime",    SelectTarget.Auto)]
    [InlineData("select delete",          "delete",          SelectTarget.Auto)]  // keyword as name
    [InlineData("select search",          "search",          SelectTarget.Auto)]
    [InlineData("select folder Work",     "Work",            SelectTarget.Folder)]
    [InlineData("select folder My Work",  "My Work",         SelectTarget.Folder)]
    [InlineData("select entry gmail",     "gmail",           SelectTarget.Entry)]
    [InlineData("select entry Amazon Prime","Amazon Prime",  SelectTarget.Entry)]
    public void Select_command(string input, string name, SelectTarget target)
    {
        var cmd = Parse<SelectCommand>(input);
        Assert.Equal(name,   cmd.Name,   ignoreCase: true);
        Assert.Equal(target, cmd.Target);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 10. add/create/new entry
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("add gmail",           "gmail")]
    [InlineData("create Amazon Prime", "Amazon Prime")]
    [InlineData("new my work account", "my work account")]
    // keyword as entry name
    [InlineData("add search",          "search")]
    [InlineData("add list",            "list")]
    [InlineData("add back",            "back")]
    public void AddEntry_shorthand(string input, string name)
    {
        var cmd = Parse<AddEntryCommand>(input);
        Assert.Equal(name, cmd.Name, ignoreCase: true);
    }

    [Theory]
    [InlineData("add entry gmail",           "gmail")]
    [InlineData("create entry Amazon Prime", "Amazon Prime")]
    [InlineData("new entry work account",    "work account")]
    public void AddEntry_explicit(string input, string name)
    {
        var cmd = Parse<AddEntryCommand>(input);
        Assert.Equal(name, cmd.Name, ignoreCase: true);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("create")]
    [InlineData("new")]
    public void AddEntry_interactive_bare(string input)
    {
        var cmd = Parse<AddEntryCommand>(input);
        Assert.Null(cmd.Name);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 11. add/create folder
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("add folder Work",           "Work")]
    [InlineData("create folder My Projects", "My Projects")]
    [InlineData("new folder Finance",        "Finance")]
    [InlineData("folder Social Media",       "Social Media")]  // bare "folder <name>"
    public void AddFolder_with_name(string input, string name)
    {
        var cmd = Parse<AddFolderCommand>(input);
        Assert.Equal(name, cmd.Name, ignoreCase: true);
    }

    [Theory]
    [InlineData("add folder")]
    [InlineData("create folder")]
    [InlineData("new folder")]
    [InlineData("folder")]
    public void AddFolder_prompt(string input)
    {
        var cmd = Parse<AddFolderCommand>(input);
        Assert.Null(cmd.Name);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 12. delete / del / rm / remove
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("delete gmail",        "gmail")]
    [InlineData("del amazon",          "amazon")]
    [InlineData("rm old entry",        "old entry")]
    [InlineData("remove My Account",   "My Account")]
    // keyword as name
    [InlineData("delete search",       "search")]
    [InlineData("delete list",         "list")]
    public void Delete_by_name(string input, string name)
    {
        var cmd = Parse<DeleteCommand>(input);
        Assert.Equal(name, cmd.Name, ignoreCase: true);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("del")]
    [InlineData("rm")]
    [InlineData("remove")]
    public void Delete_interactive(string input)
    {
        var cmd = Parse<DeleteCommand>(input);
        Assert.Null(cmd.Name);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 13. edit / update / modify (entry-level, not field-set)
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("edit gmail",     "gmail")]
    [InlineData("update amazon",  "amazon")]
    [InlineData("modify github",  "github")]  // "modify" without "to <val>" → EditCommand
    public void Edit_by_name(string input, string name)
    {
        var cmd = Parse<EditCommand>(input);
        Assert.Equal(name, cmd.Name, ignoreCase: true);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("update")]
    [InlineData("modify")]
    public void Edit_interactive(string input)
    {
        var cmd = Parse<EditCommand>(input);
        Assert.Null(cmd.Name);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 14. search / find
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("search google",    "google")]
    [InlineData("find amazon",      "amazon")]
    [InlineData("search my email",  "my email")]
    public void Search_with_term(string input, string term)
    {
        var cmd = Parse<SearchCommand>(input);
        Assert.Equal(term, cmd.Term, ignoreCase: true);
    }

    [Theory]
    [InlineData("search")]
    [InlineData("find")]
    public void Search_interactive(string input)
    {
        var cmd = Parse<SearchCommand>(input);
        Assert.Null(cmd.Term);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 15. copy
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("copy password from gmail",          "password", "gmail")]
    [InlineData("copy username from amazon",         "username", "amazon")]
    [InlineData("copy password from My Account",     "password", "My Account")]
    public void Copy_from(string input, string field, string entry)
    {
        var cmd = Parse<CopyCommand>(input);
        Assert.Equal(field, cmd.Field,     ignoreCase: true);
        Assert.Equal(entry, cmd.FromEntry, ignoreCase: true);
    }

    [Theory]
    [InlineData("copy password",  "password")]
    [InlineData("copy username",  "username")]
    [InlineData("copy url",       "url")]
    public void Copy_field_only(string input, string field)
    {
        var cmd = Parse<CopyCommand>(input);
        Assert.Equal(field, cmd.Field, ignoreCase: true);
        Assert.Null(cmd.FromEntry);
    }

    [Fact] public void Copy_interactive()
    {
        var cmd = Parse<CopyCommand>("copy");
        Assert.Null(cmd.Field);
        Assert.Null(cmd.FromEntry);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 16. Keywords usable as entry / folder names  (anyWord coverage)
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("add search")]   // "search" is a keyword but valid entry name
    [InlineData("add copy")]
    [InlineData("add list")]
    [InlineData("add back")]
    [InlineData("add exit")]
    [InlineData("add find")]
    [InlineData("add edit")]
    [InlineData("add from")]
    [InlineData("add select")]
    // Note: "add folder" is intentionally NOT here — it correctly parses as
    // AddFolderCommand(null) (bare add-folder-prompt), not AddEntryCommand.
    public void Keywords_as_entry_names(string input)
        => Parse<AddEntryCommand>(input);

    // ═══════════════════════════════════════════════════════════════════════
    // 17. Case-insensitivity smoke tests
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("ADD PASSWORD WITH VALUE S3cr3t")]
    [InlineData("Set Password To S3cr3t")]
    [InlineData("MODIFY PASSWORD TO S3cr3t")]
    [InlineData("SELECT FOLDER Work")]
    [InlineData("DELETE gmail")]
    [InlineData("SEARCH google")]
    [InlineData("COPY password FROM gmail")]
    [InlineData("LIST")]
    [InlineData("BACK")]
    [InlineData("EXIT")]
    public void Commands_are_case_insensitive(string input)
    {
        var cmd = CommandParser.Parse(input);
        Assert.False(cmd is UnknownCommand, $"Expected a known command for: {input}");
        Assert.False(cmd is EmptyCommand,   $"Expected a non-empty command for: {input}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 18. Value masking boundary — ValueStartIndex via public grammar checks
    //     (indirectly: verify that VALUE_TEXT captures multi-word values)
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("add password with value go to sleep",          "password", "go to sleep")]
    [InlineData("add password = go to sleep",                   "password", "go to sleep")]
    [InlineData("set password to go to sleep",                  "password", "go to sleep")]
    [InlineData("modify password to go to sleep",               "password", "go to sleep")]
    // "value" word inside the actual value
    [InlineData("set notes to the value of life",               "notes",    "the value of life")]
    // "with" word inside the actual value (captured because it's in VALUE_MODE)
    [InlineData("add notes with value work with passion",       "notes",    "work with passion")]
    public void Value_captures_rest_of_line_verbatim(string input, string key, string value)
    {
        var cmd = Parse<SetFieldCommand>(input);
        Assert.Equal(key,   cmd.Key,   ignoreCase: true);
        Assert.Equal(value, cmd.Value);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 19. save command
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("save")]
    [InlineData("SAVE")]
    [InlineData("Save")]
    public void Save_command(string input) => Parse<SaveCommand>(input);

    // ═══════════════════════════════════════════════════════════════════════
    // 20. Quoted field names (spaces in keys)
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("add \"security question\" with value hunter2",   "security question", "hunter2")]
    [InlineData("update \"my custom field\" with value hello",    "my custom field",   "hello")]
    [InlineData("add \"secret key\" = p@ss w0rd",                 "secret key",        "p@ss w0rd")]
    [InlineData("set \"pin code\" to 1234",                       "pin code",          "1234")]
    [InlineData("modify \"recovery email\" to user@example.com",  "recovery email",    "user@example.com")]
    [InlineData("add key \"secret key\" with value val",          "secret key",        "val")]
    [InlineData("set key \"my field\" to some value",             "my field",          "some value")]
    public void SetField_quoted_key(string input, string key, string value)
    {
        var cmd = Parse<SetFieldCommand>(input);
        Assert.Equal(key,   cmd.Key);
        Assert.Equal(value, cmd.Value);
    }

    [Theory]
    [InlineData("copy \"secret key\" from gmail",   "secret key", "gmail")]
    [InlineData("copy \"my custom field\"",          "my custom field", null)]
    public void Copy_quoted_field(string input, string field, string? entry)
    {
        var cmd = Parse<CopyCommand>(input);
        Assert.Equal(field, cmd.Field);
        if (entry is null)
            Assert.Null(cmd.FromEntry);
        else
            Assert.Equal(entry, cmd.FromEntry, ignoreCase: true);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 21. AttachFileCommand — "value from file" forms
    // ═══════════════════════════════════════════════════════════════════════

    [Theory]
    // "set … to value from <file>"
    [InlineData("set password to value from /home/user/secret.txt",       "password", "/home/user/secret.txt")]
    [InlineData("set key password to value from /home/user/secret.txt",   "password", "/home/user/secret.txt")]
    [InlineData("modify password to value from /home/user/secret.txt",    "password", "/home/user/secret.txt")]
    [InlineData("modify key password to value from /home/user/secret.txt","password", "/home/user/secret.txt")]
    // "add/update … with value from <file>"
    [InlineData("add password with value from /home/user/secret.txt",     "password", "/home/user/secret.txt")]
    [InlineData("update notes with value from /my docs/notes.txt",        "notes",    "/my docs/notes.txt")]
    [InlineData("add key notes with value from C:/secrets/file.txt",      "notes",    "C:/secrets/file.txt")]
    // case-insensitive keywords
    [InlineData("SET password TO VALUE FROM /secret.txt",                 "password", "/secret.txt")]
    [InlineData("ADD password WITH VALUE FROM /secret.txt",               "password", "/secret.txt")]
    // file path with spaces
    [InlineData("set password to value from /my secret files/pass.txt",   "password", "/my secret files/pass.txt")]
    [InlineData("add notes with value from my notes folder/notes.txt",    "notes",    "my notes folder/notes.txt")]
    public void AttachFile_parses_key_and_path(string input, string key, string filePath)
    {
        var cmd = Parse<AttachFileCommand>(input);
        Assert.Equal(key,      cmd.Key,      ignoreCase: true);
        Assert.Equal(filePath, cmd.FilePath);
    }

    [Theory]
    // "= …" form is NOT detected as file attach — provides an escape hatch for literal values
    [InlineData("add notes = from /path/to/file",   "notes", "from /path/to/file")]
    [InlineData("add notes = value from /path",     "notes", "value from /path")]
    // "to …" without "value from" prefix stays as SetFieldCommand
    [InlineData("set password to hunter2",          "password", "hunter2")]
    [InlineData("set notes to from the top",        "notes",    "from the top")]
    // "with value …" without "from" prefix stays as SetFieldCommand
    [InlineData("add password with value S3cr3t",   "password", "S3cr3t")]
    public void Non_file_forms_remain_SetFieldCommand(string input, string key, string value)
    {
        var cmd = Parse<SetFieldCommand>(input);
        Assert.Equal(key,   cmd.Key,   ignoreCase: true);
        Assert.Equal(value, cmd.Value);
    }
}
