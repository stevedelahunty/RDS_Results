using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace RDS_Display;

public partial class MainWindow : Window
{
    readonly ObservableCollection<ShownRead> _reads = new();
    CancellationTokenSource? _session;
    bool _closing;
    bool _started;

    public MainWindow()
    {
        InitializeComponent();
        ReadsGrid.ItemsSource = _reads;
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

        string host = HostBox.Text.Trim();
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            StatusText.Text = "Port must be a number from 1 to 65535.";
            return;
        }

        var cts = new CancellationTokenSource();
        _session = cts;
        _ = Task.Run(() => DisplaySession.RunAsync(host, port, OnLineAsync, OnStatusAsync, cts.Token));
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
                StatusText.Text = "Connected.";
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

            ShowFeatured(_reads[0]);
        }
    }

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
            if (!string.IsNullOrWhiteSpace(settings.Host))
                HostBox.Text = settings.Host;
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
                Host = HostBox.Text.Trim(),
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
