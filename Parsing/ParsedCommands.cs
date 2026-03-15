namespace mykeepass.Parsing;

public interface ICommand { }

public enum SelectTarget { Auto, Folder, Entry }

public sealed record SetFieldCommand(string Key, string Value) : ICommand;
public sealed record CopyCommand(string? Field, string? FromEntry) : ICommand;
public sealed record SelectCommand(string Name, SelectTarget Target) : ICommand;
public sealed record BackCommand : ICommand;
public sealed record ExitCommand : ICommand;
public sealed record ListCommand : ICommand;
public sealed record SaveCommand : ICommand;
public sealed record AddEntryCommand(string? Name) : ICommand;
public sealed record AddFolderCommand(string? Name) : ICommand;
public sealed record DeleteCommand(string? Name) : ICommand;
public sealed record DeleteFolderCommand(string Name) : ICommand;
public sealed record MoveCommand(string? Name, string Destination, bool IsFolder) : ICommand;
public sealed record RenameCommand(string? Name, string NewName, bool IsFolder) : ICommand;
public sealed record EditCommand(string? Name) : ICommand;
public sealed record SearchCommand(string? Term) : ICommand;
public sealed record AttachFileCommand(string Key, string FilePath) : ICommand;
public sealed record EmptyCommand : ICommand;
public sealed record UnknownCommand(string Input) : ICommand;
