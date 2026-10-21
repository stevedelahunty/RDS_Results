using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RDS_Display;

public partial class MainWindow : Window
{
    readonly ObservableCollection<ShownRead> _reads = new();
    readonly ObservableCollection<ShownBoardRow> _boardRows = new();
    readonly List<ShownBoardPage> _boardPages = new();
    readonly DispatcherTimer _boardTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    CancellationTokenSource? _session;
    bool _closing;
    bool _started;
    string _mode = JumbotronModes.BibReads;
    int _boardPage;
    BoardPhase _boardPhase = BoardPhase.HoldTop;
    DateTime _boardHoldUntil;

    public MainWindow()
    {
        InitializeComponent();
        ReadsGrid.ItemsSource = _reads;
        BoardRows.ItemsSource = _boardRows;
        _boardTimer.Tick += (_, _) => TickBoard();
        ApplyMode(JumbotronModes.BibReads);
        LoadSettings();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_started)
            return;
        _started = true;
        Restart();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _boardTimer.Stop();
        SaveSettings();
        Stop();
        base.OnClosed(e);
    }

    void Endpoint_LostFocus(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        Restart();
    }

    void Restart()
    {
        Stop();
        if (_closing)
            return;

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            StatusText.Text = "Listen port must be a number from 1 to 65535.";
            return;
        }

        var cts = new CancellationTokenSource();
        _session = cts;
        _ = Task.Run(() => DisplaySession.RunAsync(port, OnLineAsync, OnStatusAsync, cts.Token));
    }

    void Stop()
    {
        try
        {
            _session?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        _session = null;
    }

    Task OnStatusAsync(string status)
    {
        if (_closing)
            return Task.CompletedTask;
        return Dispatcher.InvokeAsync(() =>
        {
            if (!_closing)
                StatusText.Text = status;
        }).Task;
    }

    Task OnLineAsync(string line)
    {
        if (_closing)
            return Task.CompletedTask;
        return Dispatcher.InvokeAsync(() => ApplyLine(line)).Task;
    }

    void ApplyLine(string line)
    {
        if (_closing || line.Length == 0)
            return;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            StatusText.Text = "Ignored a line that was not JSON.";
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            var ty = Text(root, "ty");
            if (ty == "hello")
            {
                _reads.Clear();
                ShowFeatured(null);
                _boardPages.Clear();
                ShowBoardPage(0);
                ApplyMode(Text(root, "mode"));
                StatusText.Text = "Connected.";
                return;
            }

            if (ty == "mode")
            {
                ApplyMode(Text(root, "mode"));
                return;
            }

            if (ty == "welcome")
            {
                WelcomeTitle.Text = Text(root, "title");
                WelcomeDetail.Text = Text(root, "detail");
                if (WelcomeTitle.Text.Length == 0)
                    WelcomeTitle.Text = "Welcome";
                return;
            }

            if (ty == "eventWelcome")
            {
                ApplyEventWelcome(root);
                return;
            }

            if (ty == "board")
            {
                ApplyBoard(root);
                return;
            }

            if (ty == "ping")
            {
                StatusText.Text = "Connected · " + DateTime.Now.ToString("HH:mm:ss");
                return;
            }

            if (ty != "read")
                return;

            var read = new ShownRead
            {
                Id = Int(root, "id"),
                ReadTime = Text(root, "readTime"),
                Location = Text(root, "location"),
                Bib = Text(root, "bib"),
                Chip = Text(root, "chip"),
                Name = Text(root, "name"),
                City = Text(root, "city"),
                State = Text(root, "state"),
                EventName = Text(root, "event"),
                Place = Text(root, "place"),
                FinishTime = Text(root, "finishTime"),
                ClockTime = Text(root, "clockTime"),
                Pace = Text(root, "pace"),
                Division = Text(root, "division"),
                DivisionPlace = Text(root, "divisionPlace"),
                AgeGroup = Text(root, "ageGroup"),
                Gender = Text(root, "gender"),
                Age = Text(root, "age"),
                Team = Text(root, "team"),
                Report = Text(root, "report"),
                Message = Text(root, "message")
            };

            var index = -1;
            for (var i = 0; i < _reads.Count; i++)
            {
                if (_reads[i].Id == read.Id)
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0)
                _reads[index] = read;
            else
                _reads.Insert(0, read);

            while (_reads.Count > 400)
                _reads.RemoveAt(_reads.Count - 1);

            if (_mode == JumbotronModes.BibReads)
                ShowFeatured(_reads[0]);
        }
    }

    void ApplyMode(string mode)
    {
        var described = JumbotronModes.Describe(mode);
        _mode = described.Id;
        ModeText.Text = described.Label;
        ModeTitle.Text = described.Label;
        ModeDetail.Text = described.Detail;
        WelcomeStage.Visibility = VisibleIf(_mode == JumbotronModes.Welcome);
        EventWelcomeStage.Visibility = VisibleIf(_mode == JumbotronModes.EventWelcome);
        BoardStage.Visibility = VisibleIf(_mode == JumbotronModes.ScrollingResults);
        ReadsStage.Visibility = VisibleIf(_mode == JumbotronModes.BibReads);
        var placeholder = _mode is not JumbotronModes.Welcome
            and not JumbotronModes.EventWelcome
            and not JumbotronModes.ScrollingResults
            and not JumbotronModes.BibReads;
        ModeStage.Visibility = VisibleIf(placeholder);
        if (_mode == JumbotronModes.BibReads)
            ShowFeatured(_reads.Count > 0 ? _reads[0] : null);
        if (_mode == JumbotronModes.ScrollingResults)
            StartBoard();
        else
            _boardTimer.Stop();
    }

    static Visibility VisibleIf(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    void ApplyEventWelcome(JsonElement root)
    {
        EventTitle.Text = Or(Text(root, "title"), "Event welcome");
        EventStart.Text = Text(root, "start");
        EventEvents.Text = Text(root, "events");
        EventHint.Text = Text(root, "hint");
        EventUrl.Text = Text(root, "resultsUrl");
        EventImage.Source = ImageFromBase64(Text(root, "image"));
        EventStart.Visibility = VisibleIf(EventStart.Text.Length > 0);
        EventEvents.Visibility = VisibleIf(EventEvents.Text.Length > 0);
        EventHint.Visibility = VisibleIf(EventHint.Text.Length > 0);
        EventUrl.Visibility = VisibleIf(EventUrl.Text.Length > 0);
    }

    void ApplyBoard(JsonElement root)
    {
        var incoming = new List<ShownBoardPage>();
        if (root.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
        {
            foreach (var page in pages.EnumerateArray())
            {
                var rows = new List<ShownBoardRow>();
                if (page.TryGetProperty("rows", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (var row in list.EnumerateArray())
                    {
                        rows.Add(new ShownBoardRow
                        {
                            Place = Text(row, "place"),
                            Bib = Text(row, "bib"),
                            Name = Text(row, "name"),
                            FinishTime = Text(row, "finishTime"),
                            City = Text(row, "city")
                        });
                    }
                }

                incoming.Add(new ShownBoardPage
                {
                    Title = Text(page, "title"),
                    Rows = rows
                });
            }
        }

        if (SameBoard(_boardPages, incoming))
            return;

        _boardPages.Clear();
        _boardPages.AddRange(incoming);
        ShowBoardPage(0);
        if (_mode == JumbotronModes.ScrollingResults)
            StartBoard();
    }

    static bool SameBoard(IReadOnlyList<ShownBoardPage> left, IReadOnlyList<ShownBoardPage> right)
    {
        if (left.Count != right.Count)
            return false;
        for (var i = 0; i < left.Count; i++)
        {
            if (left[i].Title != right[i].Title || left[i].Rows.Count != right[i].Rows.Count)
                return false;
            for (var j = 0; j < left[i].Rows.Count; j++)
            {
                var a = left[i].Rows[j];
                var b = right[i].Rows[j];
                if (a.Place != b.Place || a.Bib != b.Bib || a.Name != b.Name
                    || a.FinishTime != b.FinishTime || a.City != b.City)
                    return false;
            }
        }

        return true;
    }

    void StartBoard()
    {
        if (_boardPages.Count == 0)
        {
            BoardTitle.Text = "Waiting for standings";
            _boardRows.Clear();
            _boardTimer.Stop();
            return;
        }

        _boardPhase = BoardPhase.HoldTop;
        _boardHoldUntil = DateTime.UtcNow.AddSeconds(2.5);
        _boardTimer.Start();
    }

    void ShowBoardPage(int index)
    {
        if (_boardPages.Count == 0)
        {
            BoardTitle.Text = "Waiting for standings";
            _boardRows.Clear();
            return;
        }

        _boardPage = ((index % _boardPages.Count) + _boardPages.Count) % _boardPages.Count;
        var page = _boardPages[_boardPage];
        BoardTitle.Text = page.Title.Length == 0 ? "Results" : page.Title;
        _boardRows.Clear();
        foreach (var row in page.Rows)
            _boardRows.Add(row);
        BoardScroller.ScrollToVerticalOffset(0);
    }

    void TickBoard()
    {
        if (_closing || _mode != JumbotronModes.ScrollingResults || _boardPages.Count == 0)
        {
            _boardTimer.Stop();
            return;
        }

        var now = DateTime.UtcNow;
        if (_boardPhase == BoardPhase.HoldTop || _boardPhase == BoardPhase.HoldBottom)
        {
            if (now < _boardHoldUntil)
                return;
            if (_boardPhase == BoardPhase.HoldBottom)
            {
                ShowBoardPage(_boardPage + 1);
                _boardPhase = BoardPhase.HoldTop;
                _boardHoldUntil = now.AddSeconds(2.5);
                return;
            }

            if (BoardScroller.ScrollableHeight < 8)
            {
                _boardPhase = BoardPhase.HoldBottom;
                _boardHoldUntil = now.AddSeconds(6);
                return;
            }

            _boardPhase = BoardPhase.Scroll;
            return;
        }

        var next = BoardScroller.VerticalOffset + 1.8;
        if (next >= BoardScroller.ScrollableHeight)
        {
            BoardScroller.ScrollToVerticalOffset(BoardScroller.ScrollableHeight);
            _boardPhase = BoardPhase.HoldBottom;
            _boardHoldUntil = now.AddSeconds(2.5);
            return;
        }

        BoardScroller.ScrollToVerticalOffset(next);
    }

    static BitmapImage? ImageFromBase64(string data)
    {
        if (data.Length == 0)
            return null;
        try
        {
            var bytes = Convert.FromBase64String(data);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    static string Or(string value, string fallback) => value.Length == 0 ? fallback : value;

    void ShowFeatured(ShownRead? read)
    {
        if (read == null || (read.Bib.Length == 0 && read.Name.Length == 0))
        {
            FeaturedBib.Text = "—";
            FeaturedName.Text = read == null ? "Waiting for a bib read." : "Bib read";
            FeaturedDetail.Text = read == null ? "" : Detail(read);
            return;
        }

        FeaturedBib.Text = read.Bib.Length == 0 ? "—" : read.Bib;
        FeaturedName.Text = read.Name.Length == 0 ? "Bib " + read.Bib : read.Name;
        FeaturedDetail.Text = Detail(read);
    }

    static string Detail(ShownRead read)
    {
        var parts = new List<string>();
        if (read.Location.Length > 0)
            parts.Add(read.Location);
        if (read.FinishTime.Length > 0)
            parts.Add("Finish " + read.FinishTime);
        else if (read.ClockTime.Length > 0)
            parts.Add("Clock " + read.ClockTime);
        if (read.Place.Length > 0)
            parts.Add("Place " + read.Place);
        if (read.EventName.Length > 0)
            parts.Add(read.EventName);
        if (read.City.Length > 0 || read.State.Length > 0)
            parts.Add(string.Join(", ", new[] { read.City, read.State }.Where(part => part.Length > 0)));
        return string.Join("   ·   ", parts);
    }

    static string Text(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return "";
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString();
    }

    static int Int(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return 0;
        return value.TryGetInt32(out var number) ? number : 0;
    }

    void LoadSettings()
    {
        try
        {
            var path = SettingsPath();
            if (!File.Exists(path))
                return;
            var settings = JsonSerializer.Deserialize<DisplaySettings>(File.ReadAllText(path));
            if (settings == null)
                return;
            if (settings.Port is > 0 and <= 65535)
                PortBox.Text = settings.Port.ToString();
        }
        catch (Exception)
        {
        }
    }

    void SaveSettings()
    {
        try
        {
            var settings = new DisplaySettings
            {
                Port = int.TryParse(PortBox.Text.Trim(), out var port) ? port : 9100
            };
            var path = SettingsPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(settings));
        }
        catch (Exception)
        {
        }
    }

    static string SettingsPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RDS_Display", "settings.json");
}

enum BoardPhase
{
    HoldTop,
    Scroll,
    HoldBottom
}

public sealed class ShownBoardPage
{
    public string Title { get; init; } = "";
    public List<ShownBoardRow> Rows { get; init; } = new();
}

public sealed class ShownBoardRow
{
    public string Place { get; init; } = "";
    public string Bib { get; init; } = "";
    public string Name { get; init; } = "";
    public string FinishTime { get; init; } = "";
    public string City { get; init; } = "";
}

public sealed class ShownRead
{
    public int Id { get; init; }
    public string ReadTime { get; init; } = "";
    public string Location { get; init; } = "";
    public string Bib { get; init; } = "";
    public string Chip { get; init; } = "";
    public string Name { get; init; } = "";
    public string City { get; init; } = "";
    public string State { get; init; } = "";
    public string EventName { get; init; } = "";
    public string Place { get; init; } = "";
    public string FinishTime { get; init; } = "";
    public string ClockTime { get; init; } = "";
    public string Pace { get; init; } = "";
    public string Division { get; init; } = "";
    public string DivisionPlace { get; init; } = "";
    public string AgeGroup { get; init; } = "";
    public string Gender { get; init; } = "";
    public string Age { get; init; } = "";
    public string Team { get; init; } = "";
    public string Report { get; init; } = "";
    public string Message { get; init; } = "";
}

public sealed class DisplaySettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9100;
}
