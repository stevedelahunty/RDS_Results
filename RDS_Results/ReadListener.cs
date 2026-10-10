using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace RDS_Results;

public sealed class BibHit
{
    public string ReadTime { get; init; } = "";
    public string Bib { get; init; } = "";
    public string Chip { get; init; } = "";
    public string Name { get; init; } = "";
    public string City { get; init; } = "";
    public string State { get; init; } = "";
    public string EventName { get; init; } = "";
    public string Location { get; init; } = "";
    public string Message { get; init; } = "";
    public string AgeGroup { get; init; } = "";
    public string Gender { get; init; } = "";
    public string Age { get; init; } = "";
    public string Team { get; init; } = "";
}

public readonly record struct ListenSettings(string Host, int Port, string Password);

public readonly record struct AnnouncerLocation(int Id, string Name);

public sealed class LiveSocket
{
    readonly SemaphoreSlim _gate = new(1, 1);
    ClientWebSocket? _socket;
    CancellationToken _cancellation;

    public void Attach(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        _socket = socket;
        _cancellation = cancellationToken;
    }

    public void Detach() => _socket = null;

    public async Task SendAsync(string json)
    {
        var socket = _socket;
        var cancellation = _cancellation;
        if (socket == null || socket.State != WebSocketState.Open)
            return;
        var bytes = Encoding.UTF8.GetBytes(json);
        await _gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (_socket == null || _socket.State != WebSocketState.Open)
                return;
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellation).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}

public static class ReadListener
{
    const string Fields =
        "\"bib\",\"bib_num\",\"chip\",\"chip_num\",\"name\",\"message\",\"location\",\"event\",\"age_group\",\"city\",\"state\",\"timestamp\",\"gender\",\"age\",\"gender_age\",\"team_name\"";

    public static async Task RunAsync(
        ListenSettings settings,
        Func<BibHit, Task> onRead,
        Func<string, Task> onStatus,
        Func<IReadOnlyList<AnnouncerLocation>, Task<IReadOnlyList<int>>> onLocations,
        LiveSocket live,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(settings.Password))
                {
                    await onStatus("Enter the RaceDay hub password in Listen password. That is separate from the HTTP connector password.").ConfigureAwait(false);
                    break;
                }

                var uri = BuildUri(settings);
                await onStatus("Connecting to " + uri.GetLeftPart(UriPartial.Path)).ConfigureAwait(false);
                using var socket = new ClientWebSocket();
                socket.Options.Proxy = null;
                await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
                live.Attach(socket, cancellationToken);
                try
                {
                    await onStatus("Connected. Waiting for locations.").ConfigureAwait(false);
                    await live.SendAsync("{\"ty\":\"ping\"}").ConfigureAwait(false);
                    await live.SendAsync("{\"ty\":\"field_selection\",\"data\":[" + Fields + "]}").ConfigureAwait(false);

                    while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
                    {
                        var body = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false);
                        if (body == null)
                            break;
                        await HandleAsync(body, onRead, onStatus, onLocations, live, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    live.Detach();
                }
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested || ex is OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException ex) when (ex.Message.Contains("401", StringComparison.Ordinal))
            {
                await onStatus("The hub password was rejected. Set Listen password to the hub password in RaceDay Scoring. The HTTP password does not open this socket.").ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                await onStatus("Bib listen failed. " + ex.Message).ConfigureAwait(false);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public static Uri BuildUri(ListenSettings settings)
    {
        var host = settings.Host.Trim();
        if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            host = host["http://".Length..];
        host = ConnectAddress.Host(host.TrimEnd('/'));
        if (host.Length == 0)
            throw new InvalidOperationException("Enter the connector host.");
        if (settings.Port is < 1 or > 65535)
            throw new InvalidOperationException("Listen port must be between 1 and 65535.");
        var password = settings.Password.Trim();
        return new Uri($"ws://{host}:{settings.Port}/?authorization={Uri.EscapeDataString(password)}&client=RDS_Results");
    }

    static async Task HandleAsync(
        string body,
        Func<BibHit, Task> onRead,
        Func<string, Task> onStatus,
        Func<IReadOnlyList<AnnouncerLocation>, Task<IReadOnlyList<int>>> onLocations,
        LiveSocket live,
        CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            await onStatus("Ignored a non-JSON listen message.").ConfigureAwait(false);
            return;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("ty", out var tyElement))
                return;
            var ty = tyElement.GetString();
            if (ty == "locations")
            {
                await SelectLocationsAsync(document.RootElement, onLocations, live, onStatus, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (ty != "read" || !document.RootElement.TryGetProperty("data", out var data))
                return;
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("data", out var nested))
                data = nested;
            await onRead(ReadHit(data)).ConfigureAwait(false);
        }
    }

    static async Task SelectLocationsAsync(
        JsonElement root,
        Func<IReadOnlyList<AnnouncerLocation>, Task<IReadOnlyList<int>>> onLocations,
        LiveSocket live,
        Func<string, Task> onStatus,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            await onStatus("Location list was empty.").ConfigureAwait(false);
            return;
        }

        var offered = new List<AnnouncerLocation>();
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !TryLocationId(item, out var id))
                continue;
            var name = LocationName(item);
            offered.Add(new AnnouncerLocation(id, name.Length == 0 ? id.ToString() : name));
        }

        if (offered.Count == 0)
        {
            await onStatus("No bib location selected. No locations were offered.").ConfigureAwait(false);
            return;
        }

        var selected = await onLocations(offered).ConfigureAwait(false);
        await SendLocationSelectionAsync(live, selected, onStatus, cancellationToken).ConfigureAwait(false);
    }

    public static Task SendLocationSelectionAsync(
        LiveSocket live,
        IReadOnlyList<int> ids,
        Func<string, Task> onStatus,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.CompletedTask;
        return SendSelectionAsync(live, ids, onStatus);
    }

    static async Task SendSelectionAsync(LiveSocket live, IReadOnlyList<int> ids, Func<string, Task> onStatus)
    {
        await live.SendAsync("{\"ty\":\"location_selection\",\"data\":[" + string.Join(",", ids) + "]}").ConfigureAwait(false);
        if (ids.Count == 0)
            await onStatus("No locations are checked.").ConfigureAwait(false);
    }

    static string LocationName(JsonElement item)
    {
        foreach (var key in new[] { "timing_location_name", "name" })
        {
            if (item.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String)
                return (element.GetString() ?? "").Trim();
        }
        return "";
    }

    static bool TryLocationId(JsonElement item, out int id)
    {
        id = 0;
        foreach (var key in new[] { "timing_location_id", "id" })
        {
            if (!item.TryGetProperty(key, out var element))
                continue;
            if (element.TryGetInt32(out id))
                return true;
            if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out id))
                return true;
        }
        return false;
    }

    static BibHit ReadHit(JsonElement data)
    {
        var timestamp = Text(data, "timestamp");
        string readTime;
        if (DateTime.TryParse(timestamp, out var parsed))
            readTime = parsed.ToString("HH:mm:ss");
        else
            readTime = timestamp;

        return new BibHit
        {
            ReadTime = readTime,
            Bib = First(data, "bib", "bib_num", "read_bib"),
            Chip = First(data, "chip", "chip_num", "read_chip"),
            Name = Text(data, "name"),
            City = Text(data, "city"),
            State = Text(data, "state"),
            EventName = Text(data, "event"),
            Location = Text(data, "location"),
            Message = Text(data, "message"),
            AgeGroup = Text(data, "age_group"),
            Gender = Text(data, "gender"),
            Age = Text(data, "age"),
            Team = Text(data, "team_name")
        };
    }

    static string First(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var text = Text(element, name);
            if (text.Length > 0)
                return text;
        }
        return "";
    }

    static string Text(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => (value.GetString() ?? "").Trim(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                break;
        }
        if (stream.Length == 0)
            return "";
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }
}
