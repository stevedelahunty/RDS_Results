using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RDS_Results;

public sealed class RaceDayApiException : Exception
{
    public int StatusCode { get; }
    public string Path { get; }
    public string ResponseBody { get; }

    public RaceDayApiException(int statusCode, string path, string responseBody)
        : base(BuildMessage(statusCode, path, responseBody))
    {
        StatusCode = statusCode;
        Path = path;
        ResponseBody = responseBody;
    }

    static string BuildMessage(int statusCode, string path, string responseBody)
    {
        var body = responseBody.Trim();
        if (body.Length > 400)
            body = body[..400] + "...";

        var hint = statusCode == 401
            ? " Check the connector password. Each request is signed with the current time, so the clock needs to be close as well."
            : "";

        if (body.Length == 0)
            return $"HTTP {statusCode} from {path}.{hint}";
        return $"HTTP {statusCode} from {path}. {body}{hint}";
    }
}

public sealed class RaceDayClient : IDisposable
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    readonly HttpClient _http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(3)
    }, disposeHandler: true)
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    public void Dispose() => _http.Dispose();

    public static Uri BuildBaseUri(string host, int port)
    {
        var trimmed = host.Trim();
        if (trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The RaceDay connector in this app is HTTP.");
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed["http://".Length..];
        trimmed = ConnectAddress.Host(trimmed.TrimEnd('/'));
        if (trimmed.Length == 0)
            throw new InvalidOperationException("Enter the connector host.");
        if (port is < 1 or > 65535)
            throw new InvalidOperationException("Port must be between 1 and 65535.");
        return new Uri($"http://{trimmed}:{port}");
    }

    public async Task<string> GetRawAsync(Uri baseUri, string password, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, path));
        request.Headers.Authorization = CreateBasicAuth(password);
        request.Headers.UserAgent.ParseAdd("RDS_Results/1.0");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new RaceDayApiException((int)response.StatusCode, path, body);
        return body;
    }

    public static RaceMetadata? ParseRace(string json) =>
        JsonSerializer.Deserialize<RaceMetadata>(json, JsonOptions);

    public static ResultsMetaDocument ParseReports(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        JsonElement array;
        ResultsMetaShape shape;

        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root;
            shape = ResultsMetaShape.Array;
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 root.TryGetProperty("reports", out var reports) &&
                 reports.ValueKind == JsonValueKind.Array)
        {
            array = reports;
            shape = ResultsMetaShape.ReportsProperty;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            var keys = string.Join(", ", root.EnumerateObject().Select(property => property.Name));
            throw new InvalidOperationException(
                $"Expected a report array, or an object with a reports array. Object keys: {keys}");
        }
        else
        {
            throw new InvalidOperationException($"Unexpected results metadata JSON ({root.ValueKind}).");
        }

        var parsed = JsonSerializer.Deserialize<List<ReportMetadata>>(array.GetRawText(), JsonOptions) ?? new();
        foreach (var report in parsed)
            report.Fields ??= new();
        return new ResultsMetaDocument { Shape = shape, Reports = parsed };
    }

    public static ReportData ParseReport(string json)
    {
        var data = JsonSerializer.Deserialize<ReportData>(json, JsonOptions) ?? new ReportData();
        data.Results ??= new();
        return data;
    }

    public static string Pretty(string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    static AuthenticationHeaderValue CreateBasicAuth(string password)
    {
        var username = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + "." + CreateNonce(8);
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(password));
        var signed = hmac.ComputeHash(Encoding.UTF8.GetBytes(username));
        var encodedPassword = Convert.ToBase64String(signed);
        var token = Convert.ToBase64String(Encoding.GetEncoding("ISO-8859-1").GetBytes(username + ":" + encodedPassword));
        return new AuthenticationHeaderValue("Basic", token);
    }

    static string CreateNonce(int length)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        Span<byte> bytes = stackalloc byte[length];
        RandomNumberGenerator.Fill(bytes);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        return new string(chars);
    }
}
