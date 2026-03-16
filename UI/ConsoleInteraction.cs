using mykeepass.Helpers;

namespace mykeepass.UI;

/// <summary>
/// Console-based implementation of <see cref="IUserInteraction"/>.
/// All I/O goes directly to <see cref="Console"/> / <see cref="ConsoleHelper"/>.
/// </summary>
internal sealed class ConsoleInteraction : IUserInteraction
{
    public void WriteLine(string text = "") => Console.WriteLine(text);

    public void WriteError(string text) => Console.Error.WriteLine(text);

    public string Prompt(string message, string defaultValue = "")
        => ConsoleHelper.Prompt(message, defaultValue);

    public string ReadPassword(string prompt = "")
    {
        if (!string.IsNullOrEmpty(prompt))
            Console.Write(prompt);
        return ConsoleHelper.ReadPassword();
    }

    public bool Confirm(string question)
    {
        Console.Write($"\n  {question} (y/n): ");
        return Console.ReadLine()?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) == true;
    }

    public int PickFromList(string prompt, IReadOnlyList<string> items)
    {
        if (items.Count == 0) return -1;

        Console.WriteLine();
        for (int i = 0; i < items.Count; i++)
            Console.WriteLine($"    [{i + 1}] {items[i]}");

        Console.Write($"\n{prompt} (1–{items.Count}): ");
        if (!int.TryParse(Console.ReadLine(), out int choice) || choice < 1 || choice > items.Count)
            return -1;
        return choice;
    }
}
