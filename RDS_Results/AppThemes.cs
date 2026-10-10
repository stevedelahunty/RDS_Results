using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace RDS_Results;

public static class AppThemes
{
    public const string Light = "Light";
    public const string Dark = "Dark";

    static readonly (string Key, Color Light, Color Dark)[] Palette =
    [
        ("WindowBrush", ColorFrom("#F3F6F9"), ColorFrom("#07111C")),
        ("PanelBrush", ColorFrom("#FFFFFF"), ColorFrom("#101A2A")),
        ("InkBrush", ColorFrom("#1B2838"), ColorFrom("#F2F6FB")),
        ("MutedBrush", ColorFrom("#5C6B7A"), ColorFrom("#93A4BA")),
        ("LineBrush", ColorFrom("#D5DDE6"), ColorFrom("#2A3A50")),
        ("AccentBrush", ColorFrom("#1F4E79"), ColorFrom("#3EC6FF")),
        ("AccentInkBrush", ColorFrom("#FFFFFF"), ColorFrom("#062033")),
        ("InputBrush", ColorFrom("#FFFFFF"), ColorFrom("#070E16")),
        ("GridHeaderBrush", ColorFrom("#E7EEF5"), ColorFrom("#162232")),
        ("GridAltBrush", ColorFrom("#F6F8FB"), ColorFrom("#0E1724")),
        ("GridLineBrush", ColorFrom("#E1E7EE"), ColorFrom("#243246")),
        ("GoodBrush", ColorFrom("#0B7A48"), ColorFrom("#148F62")),
        ("WarnBrush", ColorFrom("#A86B00"), ColorFrom("#C98412")),
        ("GoldBrush", ColorFrom("#F5C400"), ColorFrom("#F5C400")),
        ("GoldInkBrush", ColorFrom("#1A1400"), ColorFrom("#1A1400"))
    ];

    public static string Normalize(string? theme) =>
        string.Equals(theme, Light, StringComparison.OrdinalIgnoreCase) ? Light : Dark;

    public static string ReadSaved()
    {
        try
        {
            var path = SettingsFile.Path;
            if (!File.Exists(path))
                return Dark;
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
            return Normalize(settings?.Theme);
        }
        catch (Exception)
        {
            return Dark;
        }
    }

    public static void Apply(string? theme)
    {
        var dark = Normalize(theme) == Dark;
        var resources = Application.Current.Resources;
        foreach (var entry in Palette)
        {
            var brush = new SolidColorBrush(dark ? entry.Dark : entry.Light);
            brush.Freeze();
            resources[entry.Key] = brush;
        }
    }

    static Color ColorFrom(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}

public static class SettingsFile
{
    public static string Path
    {
        get
        {
            var folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RDS_Results");
            Directory.CreateDirectory(folder);
            return System.IO.Path.Combine(folder, "settings.json");
        }
    }
}
