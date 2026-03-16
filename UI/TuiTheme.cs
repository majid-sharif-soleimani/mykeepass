using System.Text.Json;
using Terminal.Gui;

namespace mykeepass.UI;

/// <summary>
/// Loads a Terminal.Gui <see cref="ColorScheme"/> from <c>theme.json</c> and
/// applies it to every global color-scheme slot so that all widgets —
/// including dialogs, MessageBox, menus, and TextField context menus —
/// automatically inherit the same palette.
///
/// Available color names (case-insensitive):
///   Black  Blue  Green  Cyan  Red  Magenta  Brown  Gray
///   DarkGray  BrightBlue  BrightGreen  BrightCyan
///   BrightRed  BrightMagenta  Yellow  White
/// </summary>
internal static class TuiTheme
{
    // ── JSON model ────────────────────────────────────────────────────────────

    private sealed class ColorPair
    {
        public string Foreground { get; init; } = "White";
        public string Background { get; init; } = "Black";
    }

    private sealed class ThemeFile
    {
        public ColorPair? Normal    { get; init; }
        public ColorPair? Focus     { get; init; }
        public ColorPair? HotNormal { get; init; }
        public ColorPair? HotFocus  { get; init; }
        public ColorPair? Disabled  { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads <c>theme.json</c> from the application directory.
    /// Falls back to the built-in dark theme if the file is absent or invalid.
    /// </summary>
    public static ColorScheme Load(string fileName = "theme.json")
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, fileName);
            if (File.Exists(path))
            {
                var tf = JsonSerializer.Deserialize<ThemeFile>(
                    File.ReadAllText(path), JsonOpts);
                if (tf is not null) return Build(tf);
            }
        }
        catch { /* malformed file — use default */ }

        return Default();
    }

    /// <summary>
    /// Replaces every entry in <see cref="Colors.ColorSchemes"/> with
    /// <paramref name="scheme"/> and sets <see cref="Application.ColorScheme"/>.
    /// This ensures dialogs, MessageBox, menus, and context menus all share
    /// the same palette without needing per-widget assignments.
    /// Must be called after <see cref="Application.Init"/>.
    /// </summary>
    public static void ApplyGlobally(ColorScheme scheme)
    {
        foreach (var key in Colors.ColorSchemes.Keys.ToArray())
            Colors.ColorSchemes[key] = scheme;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ColorScheme Build(ThemeFile tf) => new()
    {
        Normal    = Attr(tf.Normal,    "White",       "Black"),
        Focus     = Attr(tf.Focus,     "White",       "Black"),
        HotNormal = Attr(tf.HotNormal, "BrightGreen", "Black"),
        HotFocus  = Attr(tf.HotFocus,  "BrightGreen", "Black"),
        Disabled  = Attr(tf.Disabled,  "Gray",        "Black"),
    };

    private static Terminal.Gui.Attribute Attr(
        ColorPair? pair, string fallbackFg, string fallbackBg) =>
        new(ParseColor(pair?.Foreground, fallbackFg),
            ParseColor(pair?.Background, fallbackBg));

    private static Color ParseColor(string? name, string fallback) =>
        Enum.TryParse<Color>(name,     ignoreCase: true, out var c) ? c :
        Enum.TryParse<Color>(fallback, ignoreCase: true, out var d) ? d :
        Color.White;

    private static ColorScheme Default() => new()
    {
        Normal    = new Terminal.Gui.Attribute(Color.White,       Color.Black),
        Focus     = new Terminal.Gui.Attribute(Color.White,       Color.Black),
        HotNormal = new Terminal.Gui.Attribute(Color.BrightGreen, Color.Black),
        HotFocus  = new Terminal.Gui.Attribute(Color.BrightGreen, Color.Black),
        Disabled  = new Terminal.Gui.Attribute(Color.Gray,        Color.Black),
    };
}
