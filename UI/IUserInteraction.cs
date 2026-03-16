namespace mykeepass.UI;

/// <summary>
/// Abstracts all interactive I/O inside <see cref="mykeepass.CommandExecutor"/>.
/// Inject one implementation for the console path and another for the TUI path.
/// </summary>
public interface IUserInteraction
{
    void   WriteLine(string text = "");
    void   WriteError(string text);
    string Prompt(string message, string defaultValue = "");
    string ReadPassword(string prompt = "");
    Task<bool> ConfirmAsync(string question);

    /// <summary>
    /// Presents a numbered list and returns the 1-based index of the chosen item,
    /// or -1 if the user cancelled.
    /// </summary>
    int PickFromList(string prompt, IReadOnlyList<string> items);
}
