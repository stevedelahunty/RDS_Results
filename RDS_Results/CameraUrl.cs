using System.Text.RegularExpressions;

namespace RDS_Results;

public static class CameraUrl
{
    public const string DefaultTemplate = "rtsp://{ip}:554/stream1";

    public static string Resolve(AppSettings settings)
    {
        if (settings.CameraAdvanced && settings.CameraUseExact)
        {
            var exact = (settings.CameraExactUrl ?? "").Trim();
            return LooksLikeUrl(exact) ? exact : "";
        }

        var ip = (settings.CameraIp ?? "").Trim();
        if (ip.Length == 0)
            return "";
        var template = NormalizeTemplate(settings.CameraTemplate);
        if (!template.Contains("{ip}", StringComparison.OrdinalIgnoreCase))
            template = DefaultTemplate;
        return template.Replace("{ip}", ip, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeTemplate(string? template)
    {
        var trimmed = (template ?? "").Trim();
        if (trimmed.Length == 0 ||
            trimmed.Equals("rtsp://{ip}:554/stream", StringComparison.OrdinalIgnoreCase))
            return DefaultTemplate;
        return trimmed;
    }

    public static string Redact(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";
        return Regex.Replace(url, @"(//)([^/@:]+):([^/@]+)@", "$1$2:***@");
    }

    static bool LooksLikeUrl(string value) =>
        value.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
