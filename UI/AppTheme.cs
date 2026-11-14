using System.Windows;
using System.Windows.Media;
using PdbExplorer.Rendering;

namespace PdbExplorer.UI;

/// <summary>
/// Dark and light color sets modelled on Visual Studio. The brushes live in the application resources and
/// the XAML refers to them with DynamicResource, so switching theme only replaces them.
/// </summary>
internal static class AppTheme
{
    static readonly string[] DarkSyntax = ["#DCDCDC", "#569CD6", "#4EC9B0", "#57A64A", "#B5CEA8", "#DCDCAA", "#9CDCFE", "#B8D7A3", "#9A9A9A"];
    static readonly string[] LightSyntax = ["#000000", "#0000FF", "#2B91AF", "#008000", "#098658", "#74531F", "#1F377F", "#2F4F4F", "#808080"];

    static readonly (string Key, string Dark, string Light)[] Brushes =
    [
        ("Bg", "#2D2D30", "#EEEEF2"),
        ("Panel", "#252526", "#F5F5F5"),
        ("Editor", "#1E1E1E", "#FFFFFF"),
        ("EditorFg", "#DCDCDC", "#000000"),
        ("Line", "#3F3F46", "#CCCEDB"),
        ("Fg", "#F1F1F1", "#1E1E1E"),
        ("Dim", "#999999", "#717171"),
        ("Accent", "#007ACC", "#007ACC"),
        ("Hover", "#3E3E40", "#C9DEF5"),
        ("Sel", "#094771", "#99C9EF"),
        ("LineNumber", "#2B91AF", "#2B91AF"),
        ("FindMatch", "#653306", "#F8C9AB"),
        ("Thumb", "#686868", "#C2C3C9"),
        ("ThumbHover", "#9E9E9E", "#868999"),
        ("ThumbDrag", "#EFEBEF", "#5B5C68"),
        ("Track", "#3E3E42", "#E8E8EC"),
        ("Input", "#333337", "#FFFFFF"),
        ("Popup", "#1B1B1C", "#F6F6F6"),
        ("PopupLine", "#333337", "#CCCEDB"),
        ("Disabled", "#656565", "#A2A4A5"),
        ("Tip", "#424245", "#F6F6F6"),
        ("TipLine", "#4D4D50", "#CCCEDB"),
    ];

    public static bool IsDark => Settings.Current.Theme != "Light";

    /// <summary>
    /// Syntax colors indexed by <see cref="TokenKind"/>. The array is updated in place when the theme
    /// changes, so editors only need to redraw.
    /// </summary>
    public static Brush[] Palette { get; } = new Brush[DarkSyntax.Length];

    public static Brush For(TokenKind kind) => Palette[(int)kind];

    public static void Apply()
    {
        bool dark = IsDark;
        var resources = Application.Current.Resources;
        foreach (var (key, darkColor, lightColor) in Brushes)
        {
            resources[key] = Frozen(dark ? darkColor : lightColor);
        }

        // The default ScrollViewer template paints the corner between two scroll bars with this system brush.
        resources[SystemColors.ControlBrushKey] = resources["Bg"];

        string[] syntax = dark ? DarkSyntax : LightSyntax;
        for (int i = 0; i < Palette.Length; i++)
        {
            Palette[i] = Frozen(syntax[i]);
        }
    }

    static Brush Frozen(string color)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        brush.Freeze();
        return brush;
    }
}
