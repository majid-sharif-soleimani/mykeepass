using Antlr4.Runtime;
using System.IO;

namespace mykeepass.Parsing;

/// <summary>
/// Entry point for command parsing. Converts a raw input string into a typed
/// <see cref="ICommand"/> using the ANTLR4-generated lexer and parser.
/// </summary>
public static class CommandParser
{
    private static readonly CommandVisitor _visitor = new();

    /// <summary>
    /// Parses <paramref name="input"/> and returns the corresponding command.
    /// Returns <see cref="UnknownCommand"/> if the input does not match the grammar.
    /// </summary>
    public static ICommand Parse(string input)
    {
        // Reset error state before each parse so stale errors don't bleed over.
        SilentErrorListener.Instance.Reset();

        var lexer = new MyKeePassLexer(CharStreams.fromString(input));
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(SilentErrorListener.Instance);  // IAntlrErrorListener<int>

        var tokens = new CommonTokenStream(lexer);
        var parser = new MyKeePassParser(tokens);
        parser.RemoveErrorListeners();
        parser.AddErrorListener(SilentErrorListener.Instance); // IAntlrErrorListener<IToken>

        var tree = parser.command();

        // If a syntax error was reported, or if tokens remain unconsumed after
        // parsing (the EmptyCmd alternative silently matches 0 tokens on
        // unrecognised input), treat the input as unknown.
        if (SilentErrorListener.Instance.HasError
            || tokens.LT(1).Type != Antlr4.Runtime.TokenConstants.EOF)
            return new UnknownCommand(input);

        return _visitor.Visit(tree) ?? new UnknownCommand(input);
    }
}

/// <summary>
/// A shared, single-instance error listener that silently records whether a
/// syntax error occurred. Implements both the lexer variant (<c>int</c> token)
/// and the parser variant (<c>IToken</c>) so one instance serves both.
/// Thread-safety is not required — the app is single-threaded.
/// </summary>
internal sealed class SilentErrorListener : BaseErrorListener, IAntlrErrorListener<int>
{
    public static readonly SilentErrorListener Instance = new();

    public bool HasError { get; private set; }

    public void Reset() => HasError = false;

    // Parser error listener (IAntlrErrorListener<IToken> via BaseErrorListener)
    public override void SyntaxError(
        TextWriter output,
        IRecognizer recognizer,
        IToken offendingSymbol,
        int line,
        int charPositionInLine,
        string msg,
        RecognitionException e)
        => HasError = true;

    // Lexer error listener (IAntlrErrorListener<int>)
    void IAntlrErrorListener<int>.SyntaxError(
        TextWriter output,
        IRecognizer recognizer,
        int offendingSymbol,
        int line,
        int charPositionInLine,
        string msg,
        RecognitionException e)
        => HasError = true;
}
