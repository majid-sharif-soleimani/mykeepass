namespace mykeepass.Helpers;

/// <summary>Small console utilities used across the application.</summary>
public static class ConsoleHelper
{
    /// <summary>
    /// Reads a password from the console, echoing '*' for each character,
    /// and returns it as a UTF-8 <see cref="byte"/> array.
    ///
    /// The intermediate <c>char[]</c> buffer is zero-filled before this method
    /// returns, so the plaintext never lingers as a .NET <see cref="string"/>.
    /// The <b>caller</b> is responsible for zero-filling the returned array after
    /// use (e.g. with <see cref="Array.Clear"/>).
    /// </summary>
    public static byte[] ReadPasswordAsBytes()
    {
        // Pre-allocate a fixed char buffer — never converted to a string.
        // 512 chars covers any realistic master password.
        const int MaxLen = 512;
        char[] chars = new char[MaxLen];
        int length = 0;

        try
        {
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter)
                    break;

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (length > 0)
                    {
                        chars[--length] = '\0';           // zero the removed char
                        Console.Write("\b \b");
                    }
                }
                else if (key.Key != ConsoleKey.Escape && length < MaxLen - 1)
                {
                    chars[length++] = key.KeyChar;
                    Console.Write('*');
                }
            }

            Console.WriteLine();
            // Encode to UTF-8 bytes before clearing the char buffer.
            return System.Text.Encoding.UTF8.GetBytes(chars, 0, length);
        }
        finally
        {
            // Zero-fill the entire char array regardless of how we exit.
            Array.Clear(chars, 0, chars.Length);
        }
    }

    /// <summary>
    /// Reads a password from the console, echoing '*' for each character.
    /// Returns a plain <see cref="string"/>; prefer
    /// <see cref="ReadPasswordAsBytes"/> when handling master passwords.
    /// </summary>
    public static string ReadPassword()
    {
        byte[] bytes = ReadPasswordAsBytes();
        try
        {
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    /// <summary>
    /// Reverses a string that contains Persian/Arabic (RTL) characters so it
    /// displays correctly in terminals and UI frameworks that lack BiDi support.
    /// Returns the string unchanged if no RTL characters are detected.
    /// </summary>
    public static string RtlDisplay(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (char c in text)
        {
            // Arabic/Persian Unicode block: U+0600–U+06FF
            if (c >= '\u0600' && c <= '\u06FF')
            {
                var chars = text.ToCharArray();
                Array.Reverse(chars);
                return new string(chars);
            }
        }
        return text;
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
