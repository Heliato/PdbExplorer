using System.Text.Json;

namespace PdbExplorer;

/// <summary>User options, stored in %AppData%\PdbExplorer\settings.json.</summary>
internal sealed class Settings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PdbExplorer", "settings.json");

    public static Settings Current { get; } = Load();

    public string Language { get; set; } = "en";
    public string Theme { get; set; } = "Dark";
    public double FontSize { get; set; } = 13;
    public bool LineNumbers { get; set; } = true;

    /// <summary>Index the inline sites after loading, to recover parameter names of always-inlined functions.</summary>
    public bool InlineParamNames { get; set; } = true;

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Not being able to save is not worth interrupting the user: the options still apply to this session.
        }
    }

    static Settings Load()
    {
        Settings settings;
        try
        {
            settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            settings = new Settings();
        }

        // The file can be edited by hand: fall back to defaults for anything the UI cannot represent.
        if (settings.Language != "fr")
        {
            settings.Language = "en";
        }
        if (settings.Theme != "Light")
        {
            settings.Theme = "Dark";
        }
        settings.FontSize = double.IsFinite(settings.FontSize) ? Math.Clamp(settings.FontSize, 8, 32) : 13;
        return settings;
    }
}
