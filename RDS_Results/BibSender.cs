using System.Globalization;
using System.IO;
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

    CancellationTokenSource? _cts;
    bool _running;
    string _host = "127.0.0.1";
    int _port;
    int _generation;
    string _mode = JumbotronModes.BibReads;
    public Func<WeatherSnapshot?>? Weather { get; set; }
    public Func<RaceStartSnapshot?>? RaceStart { get; set; }
    string? _welcomeJson;
    string? _eventWelcomeJson;
    string? _boardJson;

    public void Stop()
    {
        CancellationTokenSource? cts;
        List<ClientSlot> slots;
        lock (_gate)
        {
            _running = false;
            cts = _cts;
            _cts = null;
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

        foreach (var slot in slots)
            slot.Outbox.Writer.TryComplete();

        cts?.Dispose();
    }

    public bool Start(string host, int port, string mode, Action<string> onStatus)
    {
        Stop();
        var connectHost = ConnectAddress.Host(host);
        if (connectHost.Length == 0)
        {
            onStatus("Enter the jumbotron host.");
            return false;
        }
        if (port is < 1 or > 65535)
        {
            onStatus("Jumbotron port must be a number from 1 to 65535.");
            return false;
        }

        var cts = new CancellationTokenSource();
        int generation;
        lock (_gate)
        {
            _cts = cts;
            _host = connectHost;
            _port = port;
            _mode = JumbotronModes.Normalize(mode);
            _running = true;
            generation = ++_generation;
        }

        onStatus(Describe(0, connectHost, port));
        _ = Task.Run(() => ConnectLoopAsync(connectHost, port, generation, onStatus, cts.Token));
        _ = Task.Run(() => PingLoopAsync(cts.Token));
        return true;
    }

    public void SetMode(string mode)
    {
        var id = JumbotronModes.Normalize(mode);
        string? extra = null;
        List<ClientSlot> slots;
        lock (_gate)
        {
            if (_mode == id)
                return;
            _mode = id;
            if (!_running)
                return;
            slots = _clients.ToList();
            extra = StageFor(id);
        }

        var line = FormatMode(id);
        foreach (var slot in slots)
        {
            slot.Outbox.Writer.TryWrite(line);
            if (extra != null)
                slot.Outbox.Writer.TryWrite(extra);
        }
    }

    string? StageFor(string mode) => mode switch
    {
        JumbotronModes.Welcome => _welcomeJson,
        JumbotronModes.EventWelcome => _eventWelcomeJson,
        JumbotronModes.ScrollingResults => _boardJson,
        _ => null
    };

    public void SetWelcome(JumbotronWelcome welcome) =>
        ReplaceStage(ref _welcomeJson, FormatWelcome(welcome));

    public void SetEventWelcome(JumbotronEventWelcome welcome) =>
        ReplaceStage(ref _eventWelcomeJson, FormatEventWelcome(welcome));

    public void SetBoard(IReadOnlyList<JumbotronBoardPage> pages) =>
        ReplaceStage(ref _boardJson, FormatBoard(pages));

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

    async Task ConnectLoopAsync(string host, int port, int generation, Action<string> onStatus, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                onStatus(Describe(0, host, port));
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;
                await RunSessionAsync(client, generation, onStatus, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                onStatus($"Jumbotron connection failed. {ex.Message} Retrying {host}:{port}.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    async Task RunSessionAsync(TcpClient client, int generation, Action<string> onStatus, CancellationToken cancellationToken)
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
                slot.Outbox.Writer.TryWrite(FormatHello(_mode));
                if (_welcomeJson != null)
                    slot.Outbox.Writer.TryWrite(_welcomeJson);
                if (_eventWelcomeJson != null)
                    slot.Outbox.Writer.TryWrite(_eventWelcomeJson);
                if (_boardJson != null)
                    slot.Outbox.Writer.TryWrite(_boardJson);
                foreach (var id in _order)
                    slot.Outbox.Writer.TryWrite(_byId[id]);
                slot.Outbox.Writer.TryWrite(BuildPing());
                count = _clients.Count;
            }

            onStatus(Describe(count, _host, _port));

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
                    onStatus(Describe(remaining, _host, _port));
            }
        }
    }

    static async Task PumpAsync(TcpClient client, ClientSlot slot, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), bufferSize: 64 * 1024, leaveOpen: true)
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
                List<ClientSlot> slots;
                lock (_gate)
                    slots = _running ? _clients.ToList() : new List<ClientSlot>();
                string ping = BuildPing();
                foreach (var slot in slots)
                    slot.Outbox.Writer.TryWrite(ping);
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    static string Describe(int count, string host, int port) => count == 0
        ? $"Connecting to {host}:{port}."
        : $"Connected to {host}:{port}.";

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

    void ReplaceStage(ref string? stored, string next)
    {
        List<ClientSlot> slots;
        lock (_gate)
        {
            if (stored == next)
                return;
            stored = next;
            if (!_running)
                return;
            slots = _clients.ToList();
        }

        foreach (var slot in slots)
            slot.Outbox.Writer.TryWrite(next);
    }

    static string FormatHello(string mode) => JsonSerializer.Serialize(new WireHello { Mode = mode });

    static string FormatMode(string mode) => JsonSerializer.Serialize(new WireMode { Mode = mode });

    static string FormatWelcome(JumbotronWelcome welcome) => JsonSerializer.Serialize(new WireWelcome
    {
        Title = welcome.Title,
        Detail = welcome.Detail
    });

    static string FormatEventWelcome(JumbotronEventWelcome welcome) => JsonSerializer.Serialize(new WireEventWelcome
    {
        Title = welcome.Title,
        Start = welcome.Start,
        Events = welcome.Events,
        Hint = welcome.Hint,
        ResultsUrl = welcome.ResultsUrl,
        ImageMime = welcome.ImageMime,
        Image = welcome.Image
    });

    static string FormatBoard(IReadOnlyList<JumbotronBoardPage> pages) => JsonSerializer.Serialize(new WireBoard
    {
        Pages = pages.Select(page => new WireBoardPage
        {
            Title = page.Title,
            Columns = page.Columns.Select(column => new WireBoardColumn
            {
                Key = column.Key,
                Label = column.Label,
                Role = column.Role
            }).ToList(),
            Rows = page.Rows.Select(row => new WireBoardRow
            {
                Cells = row.Cells,
                Place = Legacy(page.Columns, row.Cells, "place"),
                Bib = Legacy(page.Columns, row.Cells, "bib"),
                Name = Legacy(page.Columns, row.Cells, "name"),
                FinishTime = Legacy(page.Columns, row.Cells, "net", "clock"),
                Pace = Legacy(page.Columns, row.Cells, "pace"),
                City = Legacy(page.Columns, row.Cells, "city")
            }).ToList()
        }).ToList()
    });

    static string Legacy(IReadOnlyList<JumbotronBoardColumn> columns, IReadOnlyList<string> cells, params string[] roles)
    {
        foreach (var role in roles)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (columns[i].Role != role)
                    continue;
                return i < cells.Count ? cells[i] : "";
            }
        }

        return "";
    }

    static readonly JsonSerializerOptions PingJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public void SendPingNow()
    {
        List<ClientSlot> slots;
        lock (_gate)
        {
            if (!_running)
                return;
            slots = _clients.ToList();
        }

        var ping = BuildPing();
        foreach (var slot in slots)
            slot.Outbox.Writer.TryWrite(ping);
    }

    string BuildPing() => FormatPing(Weather?.Invoke(), RaceStart?.Invoke());

    static string FormatPing(WeatherSnapshot? weather, RaceStartSnapshot? raceStart)
    {
        DateTime now = DateTime.Now;
        WireWeather? wx = null;
        if (weather != null && (weather.Loc.Length > 0 || weather.Temp.Length > 0))
        {
            wx = new WireWeather
            {
                Loc = weather.Loc,
                Temp = weather.Temp,
                Cond = weather.Cond
            };
        }

        WireRaceStart? race = null;
        if (raceStart != null)
        {
            race = new WireRaceStart
            {
                Start = raceStart.Display,
                H = raceStart.At.Hour,
                M = raceStart.At.Minute,
                S = raceStart.At.Second,
                Y = raceStart.At.Year,
                Mo = raceStart.At.Month,
                D = raceStart.At.Day
            };
        }

        return JsonSerializer.Serialize(new WirePing
        {
            Time = now.ToString("h:mm tt", CultureInfo.InvariantCulture),
            Date = now.ToString("ddd MMM d", CultureInfo.InvariantCulture),
            H = now.Hour,
            M = now.Minute,
            S = now.Second,
            Race = race,
            Wx = wx
        }, PingJsonOptions);
    }

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
        [JsonPropertyName("mode")] public string Mode { get; init; } = JumbotronModes.BibReads;
    }

    sealed class WireMode
    {
        [JsonPropertyName("v")] public int V { get; init; } = 1;
        [JsonPropertyName("ty")] public string Ty { get; init; } = "mode";
        [JsonPropertyName("mode")] public string Mode { get; init; } = JumbotronModes.BibReads;
    }

    sealed class WirePing
    {
        [JsonPropertyName("v")] public int V { get; init; } = 1;
        [JsonPropertyName("ty")] public string Ty { get; init; } = "ping";
        [JsonPropertyName("time")] public string Time { get; init; } = "";
        [JsonPropertyName("date")] public string Date { get; init; } = "";
        [JsonPropertyName("h")] public int H { get; init; }
        [JsonPropertyName("m")] public int M { get; init; }
        [JsonPropertyName("s")] public int S { get; init; }
        [JsonPropertyName("race")] public WireRaceStart? Race { get; init; }
        [JsonPropertyName("wx")] public WireWeather? Wx { get; init; }
    }

    sealed class WireRaceStart
    {
        [JsonPropertyName("start")] public string Start { get; init; } = "";
        [JsonPropertyName("h")] public int H { get; init; }
        [JsonPropertyName("m")] public int M { get; init; }
        [JsonPropertyName("s")] public int S { get; init; }
        [JsonPropertyName("y")] public int Y { get; init; }
        [JsonPropertyName("mo")] public int Mo { get; init; }
        [JsonPropertyName("d")] public int D { get; init; }
    }

    sealed class WireWeather
    {
        [JsonPropertyName("loc")] public string Loc { get; init; } = "";
        [JsonPropertyName("temp")] public string Temp { get; init; } = "";
        [JsonPropertyName("cond")] public string Cond { get; init; } = "";
    }

    sealed class WireWelcome
    {
        [JsonPropertyName("v")] public int V { get; init; } = 1;
        [JsonPropertyName("ty")] public string Ty { get; init; } = "welcome";
        [JsonPropertyName("title")] public string Title { get; init; } = "";
        [JsonPropertyName("detail")] public string Detail { get; init; } = "";
    }

    sealed class WireEventWelcome
    {
        [JsonPropertyName("v")] public int V { get; init; } = 1;
        [JsonPropertyName("ty")] public string Ty { get; init; } = "eventWelcome";
        [JsonPropertyName("title")] public string Title { get; init; } = "";
        [JsonPropertyName("start")] public string Start { get; init; } = "";
        [JsonPropertyName("events")] public string Events { get; init; } = "";
        [JsonPropertyName("hint")] public string Hint { get; init; } = "";
        [JsonPropertyName("resultsUrl")] public string ResultsUrl { get; init; } = "";
        [JsonPropertyName("imageMime")] public string ImageMime { get; init; } = "";
        [JsonPropertyName("image")] public string Image { get; init; } = "";
    }

    sealed class WireBoard
    {
        [JsonPropertyName("v")] public int V { get; init; } = 1;
        [JsonPropertyName("ty")] public string Ty { get; init; } = "board";
        [JsonPropertyName("pages")] public List<WireBoardPage> Pages { get; init; } = new();
    }

    sealed class WireBoardPage
    {
        [JsonPropertyName("title")] public string Title { get; init; } = "";
        [JsonPropertyName("columns")] public List<WireBoardColumn> Columns { get; init; } = new();
        [JsonPropertyName("rows")] public List<WireBoardRow> Rows { get; init; } = new();
    }

    sealed class WireBoardColumn
    {
        [JsonPropertyName("key")] public string Key { get; init; } = "";
        [JsonPropertyName("label")] public string Label { get; init; } = "";
        [JsonPropertyName("role")] public string Role { get; init; } = "";
    }

    sealed class WireBoardRow
    {
        [JsonPropertyName("cells")] public List<string> Cells { get; init; } = new();
        [JsonPropertyName("place")] public string Place { get; init; } = "";
        [JsonPropertyName("bib")] public string Bib { get; init; } = "";
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("finishTime")] public string FinishTime { get; init; } = "";
        [JsonPropertyName("pace")] public string Pace { get; init; } = "";
        [JsonPropertyName("city")] public string City { get; init; } = "";
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
