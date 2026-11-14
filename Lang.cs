using System.Globalization;

namespace PdbExplorer;

/// <summary>
/// Texts in two languages, gettext-style: the English text is the key and the French one comes from
/// the table in Lang.French.cs. A text without an entry stays in English.
/// </summary>
internal static partial class Lang
{
    public static bool IsFrench { get; private set; }

    /// <summary>
    /// Culture used to format numbers. It is passed explicitly: WPF captures the thread culture with each
    /// queued operation, so parts of the UI would keep the old format for a while after a language change.
    /// </summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    /// <param name="language">"fr" for French, anything else for English.</param>
    public static void Select(string language)
    {
        IsFrench = language == "fr";
        Culture = CultureInfo.GetCultureInfo(IsFrench ? "fr-FR" : "en-US");

        // AvalonDock chooses the language of its own menus from the UI culture.
        CultureInfo.DefaultThreadCurrentCulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        Thread.CurrentThread.CurrentCulture = Culture;
        Thread.CurrentThread.CurrentUICulture = Culture;
    }

    public static string Tr(string english) =>
        IsFrench && French.TryGetValue(english, out string? french) ? french : english;

    public static string Format(string english, params object[] arguments) =>
        string.Format(Culture, Tr(english), arguments);
}
