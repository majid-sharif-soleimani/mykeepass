using System.Text.Json;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;

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
    public const string SchemeName = "mykeepass";

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
        public ColorPair? Editable  { get; init; }
        public ColorPair? ReadOnly  { get; init; }
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
    public static Scheme Load(string fileName = "theme.json")
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
    public static void ApplyGlobally(Scheme scheme)
    {
        if (SchemeManager.TryGetScheme(SchemeName, out _))
            SchemeManager.RemoveScheme(SchemeName);

        SchemeManager.AddScheme(SchemeName, scheme);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Scheme Build(ThemeFile tf) => new()
    {
        Normal    = Attr(tf.Normal,    "White",       "Black"),
        Focus     = Attr(tf.Focus,     "White",       "Black"),
        HotNormal = Attr(tf.HotNormal, "BrightGreen", "Black"),
        HotFocus  = Attr(tf.HotFocus,  "BrightGreen", "Black"),
        Editable  = Attr(tf.Editable,  "White",       "Black"),
        ReadOnly  = Attr(tf.ReadOnly,  "White",       "Black"),
        Disabled  = Attr(tf.Disabled,  "Gray",        "Black"),
    };

    private static Terminal.Gui.Drawing.Attribute Attr(
        ColorPair? pair, string fallbackFg, string fallbackBg) =>
        new(ParseColor(pair?.Foreground, fallbackFg),
            ParseColor(pair?.Background, fallbackBg));

    private static Color ParseColor(string? name, string fallback) =>
        Color.TryParse(name ?? fallback, out var c) ? c ?? Color.White :
        Color.TryParse(fallback, out var d) ? d ?? Color.White :
        Color.White;

    private static Scheme Default() => new()
    {
        Normal    = new Terminal.Gui.Drawing.Attribute(Color.White,       Color.Black),
        Focus     = new Terminal.Gui.Drawing.Attribute(Color.White,       Color.Black),
        HotNormal = new Terminal.Gui.Drawing.Attribute(Color.BrightGreen, Color.Black),
        HotFocus  = new Terminal.Gui.Drawing.Attribute(Color.BrightGreen, Color.Black),
        Editable  = new Terminal.Gui.Drawing.Attribute(Color.White,       Color.Black),
        ReadOnly  = new Terminal.Gui.Drawing.Attribute(Color.White,       Color.Black),
        Disabled  = new Terminal.Gui.Drawing.Attribute(Color.Gray,        Color.Black),
    };
}
