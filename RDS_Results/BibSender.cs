using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace RDS_Results;

public sealed class BibSender : IDisposable
{
    const int HistoryCap = 100;

    readonly object _gate = new();
    readonly Dictionary<int, string> _byId = new();
    readonly List<int> _order = new();
    readonly List<ClientSlot> _clients = new();

    TcpListener? _listener;
    CancellationTokenSource? _cts;
    bool _running;
    int _port;
    int _generation;

    public void Stop()
    {
        CancellationTokenSource? cts;
        TcpListener? listener;
        List<ClientSlot> slots;
        lock (_gate)
        {
            _running = false;
            cts = _cts;
            _cts = null;
            listener = _listener;
            _listener = null;
            slots = _clients.ToList();
            _clients.Clear();
            _order.Clear();
            _byId.Clear();
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            listener?.Stop();
        }
        catch (SocketException)
        {
        }

        foreach (var slot in slots)
            slot.Outbox.Writer.TryComplete();

        cts?.Dispose();
    }

    public bool Start(int port, Action<string> onStatus)
    {
        Stop();
        TcpListener listener;
        try
        {
            listener = Listen(port);
        }
        catch (Exception ex)
        {
            var busy = ex is SocketException socket && socket.SocketErrorCode == SocketError.AddressAlreadyInUse;
            onStatus(busy
                ? $"Display port {port} is already in use."
                : $"Display listener failed. {ex.Message}");
            return false;
        }

        var cts = new CancellationTokenSource();
        int generation;
        lock (_gate)
        {
            _listener = listener;
            _cts = cts;
            _port = port;
            _running = true;
            generation = ++_generation;
        }

        onStatus(Describe(0, port));
        _ = Task.Run(() => AcceptLoopAsync(listener, generation, onStatus, cts.Token));
        _ = Task.Run(() => PingLoopAsync(cts.Token));
        return true;
    }

    public void Publish(BibRead read, bool update)
    {
        var live = FormatRead(read, update);
        var stored = update ? FormatRead(read, false) : live;
        List<ClientSlot> slots;
        lock (_gate)
        {
            if (!_byId.ContainsKey(read.Id))
            {
                _order.Add(read.Id);
                while (_order.Count > HistoryCap)
                {
                    var old = _order[0];
                    _order.RemoveAt(0);
                    _byId.Remove(old);
                }
            }

            _byId[read.Id] = stored;
            if (!_running)
                return;
            slots = _clients.ToList();
        }

        foreach (var slot in slots)
            slot.Outbox.Writer.TryWrite(live);
    }

    public void Dispose() => Stop();

    static TcpListener Listen(int port)
    {
        var dual = new TcpListener(IPAddress.IPv6Any, port);
        try
        {
            dual.Server.DualMode = true;
            dual.Start();
            return dual;
        }
        catch (Exception)
        {
            try
            {
                dual.Stop();
            }
            catch (Exception)
            {
            }

            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            return listener;
        }
    }

    async Task AcceptLoopAsync(TcpListener listener, int generation, Action<string> onStatus, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                client.NoDelay = true;
                _ = Task.Run(() => RunClientAsync(client, generation, onStatus, cancellationToken));
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    async Task RunClientAsync(TcpClient client, int generation, Action<string> onStatus, CancellationToken cancellationToken)
    {
        var slot = new ClientSlot();
        using (client)
        {
            int count;
            lock (_gate)
            {
                if (!_running || generation != _generation)
                    return;
                _clients.Add(slot);
                slot.Outbox.Writer.TryWrite(HelloJson);
                foreach (var id in _order)
                    slot.Outbox.Writer.TryWrite(_byId[id]);
                count = _clients.Count;
            }

            onStatus(Describe(count, _port));

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var writing = PumpAsync(client, slot, linked.Token);
                var reading = DrainAsync(client, linked.Token);
                await Task.WhenAny(writing, reading).ConfigureAwait(false);
                linked.Cancel();
                try
                {
                    await Task.WhenAll(writing, reading).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                int remaining;
                var current = false;
                lock (_gate)
                {
                    _clients.Remove(slot);
                    remaining = _clients.Count;
                    current = _running && generation == _generation;
                }

                slot.Outbox.Writer.TryComplete();
                if (current)
                    onStatus(Describe(remaining, _port));
            }
        }
    }

    static async Task PumpAsync(TcpClient client, ClientSlot slot, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        await foreach (var line in slot.Outbox.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            await writer.WriteLineAsync(line).ConfigureAwait(false);
    }

    static async Task DrainAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        while (!cancellationToken.IsCancellationRequested && await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) != null)
        {
        }
    }

    async Task PingLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                List<ClientSlot> slots;
                lock (_gate)
                    slots = _running ? _clients.ToList() : new List<ClientSlot>();
                foreach (var slot in slots)
                    slot.Outbox.Writer.TryWrite(PingJson);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    static string Describe(int count, int port) => count switch
    {
        0 => $"Waiting for a display on port {port}.",
        1 => $"1 display connected on port {port}.",
        _ => $"{count} displays connected on port {port}."
    };

    static string FormatRead(BibRead read, bool update) => JsonSerializer.Serialize(new WireRead
    {
        Id = read.Id,
        Update = update,
        ReadTime = read.ReadTime,
        Location = read.Location,
        Bib = read.Bib,
        Chip = read.Chip,
        Name = read.Name,
        City = read.City,
        State = read.State,
        Event = read.EventName,
        Place = read.Place,
        FinishTime = read.FinishTime,
        ClockTime = read.ClockTime,
        Pace = read.Pace,
        Division = read.Division,
        DivisionPlace = read.DivisionPlace,
        AgeGroup = read.AgeGroup,
        Gender = read.Gender,
        Age = read.Age,
        Team = read.Team,
        Report = read.ReportName,
        Message = read.Message
    });

    static readonly string HelloJson = JsonSerializer.Serialize(new WireHello());
    static readonly string PingJson = JsonSerializer.Serialize(new WirePing());

    sealed class ClientSlot
    {
        public Channel<string> Outbox { get; } = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }

    sealed class WireHello
    {
        [JsonPropertyName("v")] public int V { get; init; } = 1;
        [JsonPropertyName("ty")] public string Ty { get; init; } = "hello";
        [JsonPropertyName("app")] public string App { get; init; } = "RDS_Results";
    }

    sealed class WirePing
    {
        [JsonPropertyName("v")] public int V { get; init; } = 1;
        [JsonPropertyName("ty")] public string Ty { get; init; } = "ping";
    }

    sealed class WireRead
    {
        [JsonPropertyName("v")] public int V { get; init; } = 1;
        [JsonPropertyName("ty")] public string Ty { get; init; } = "read";
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("update")] public bool Update { get; init; }
        [JsonPropertyName("readTime")] public string ReadTime { get; init; } = "";
        [JsonPropertyName("location")] public string Location { get; init; } = "";
        [JsonPropertyName("bib")] public string Bib { get; init; } = "";
        [JsonPropertyName("chip")] public string Chip { get; init; } = "";
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("city")] public string City { get; init; } = "";
        [JsonPropertyName("state")] public string State { get; init; } = "";
        [JsonPropertyName("event")] public string Event { get; init; } = "";
        [JsonPropertyName("place")] public string Place { get; init; } = "";
        [JsonPropertyName("finishTime")] public string FinishTime { get; init; } = "";
        [JsonPropertyName("clockTime")] public string ClockTime { get; init; } = "";
        [JsonPropertyName("pace")] public string Pace { get; init; } = "";
        [JsonPropertyName("division")] public string Division { get; init; } = "";
        [JsonPropertyName("divisionPlace")] public string DivisionPlace { get; init; } = "";
        [JsonPropertyName("ageGroup")] public string AgeGroup { get; init; } = "";
        [JsonPropertyName("gender")] public string Gender { get; init; } = "";
        [JsonPropertyName("age")] public string Age { get; init; } = "";
        [JsonPropertyName("team")] public string Team { get; init; } = "";
        [JsonPropertyName("report")] public string Report { get; init; } = "";
        [JsonPropertyName("message")] public string Message { get; init; } = "";
    }
}
