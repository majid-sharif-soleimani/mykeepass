namespace mykeepass.Helpers;

/// <summary>Small console utilities used across the application.</summary>
public static class ConsoleHelper
{
    /// <summary>
    /// Reads a password from the console, echoing '*' for each character.
    /// The value is kept only in a local <see cref="System.Text.StringBuilder"/>
    /// and is never written to disk.
    /// </summary>
    public static string ReadPassword()
    {
        var sb = new System.Text.StringBuilder();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
                break;

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Remove(sb.Length - 1, 1);
                    Console.Write("\b \b"); // erase the last '*' character
                }
            }
            else if (key.Key != ConsoleKey.Escape)
            {
                sb.Append(key.KeyChar);
                Console.Write('*');
            }
        }

        Console.WriteLine();
        return sb.ToString();
    }

    /// <summary>
    /// Writes <paramref name="message"/> and returns the trimmed user input.
    /// If the user presses Enter with no input, <paramref name="defaultValue"/> is returned.
    /// </summary>
    public static string Prompt(string message, string defaultValue = "")
    {
        if (!string.IsNullOrEmpty(defaultValue))
            Console.Write($"{message} [{defaultValue}]: ");
        else
            Console.Write($"{message}: ");

        string? input = Console.ReadLine()?.Trim();
        return string.IsNullOrEmpty(input) ? defaultValue : input;
    }
}
