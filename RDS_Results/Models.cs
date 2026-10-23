using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace RDS_Results;

public sealed class RaceMetadata
{
    [JsonPropertyName("race")]
    public RaceInfo? Race { get; set; }

    [JsonPropertyName("events")]
    public List<RaceEvent>? Events { get; set; }
}

public sealed class RaceInfo
{
    [JsonPropertyName("rdgo_race_id")]
    public int RdgoRaceId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("start_date")]
    public string? StartDate { get; set; }

    [JsonPropertyName("end_date")]
    public string? EndDate { get; set; }

    [JsonPropertyName("integration_type")]
    public int IntegrationType { get; set; }

    [JsonPropertyName("integration_unique_key")]
    public string? IntegrationUniqueKey { get; set; }
}

public sealed class RaceEvent
{
    [JsonPropertyName("event_id")]
    public string? EventId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("distance")]
    public string? Distance { get; set; }

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }
}

public sealed class ReportMetadata
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("race_name")]
    public string? RaceName { get; set; }

    [JsonPropertyName("rdgo_race_id")]
    public int RdgoRaceId { get; set; }

    [JsonPropertyName("report_id")]
    public int ReportId { get; set; }

    [JsonPropertyName("report_builder_config_id")]
    public int ReportBuilderConfigId { get; set; }

    [JsonPropertyName("revision")]
    public int Revision { get; set; }

    [JsonPropertyName("fields")]
    public List<FieldDef>? Fields { get; set; }
}

public sealed class FieldDef
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("columnHeader")]
    public string? ColumnHeader { get; set; }

    [JsonPropertyName("columnId")]
    public string? ColumnId { get; set; }
}

public sealed class ReportData
{
    [JsonPropertyName("results")]
    public List<List<System.Text.Json.JsonElement>>? Results { get; set; }
}

public enum ResultsMetaShape
{
    Array,
    ReportsProperty
}

public sealed class ResultsMetaDocument
{
    public required ResultsMetaShape Shape { get; init; }
    public required List<ReportMetadata> Reports { get; init; }
}

public static class ReportKinds
{
    public static string FromName(string? name)
    {
        if (name == null)
            return "Unknown";
        if (name.Contains("Overall", StringComparison.Ordinal))
            return "Overall";
        if (name.Contains("Finisher", StringComparison.Ordinal))
            return "Finished";
        if (name.Contains("Progress", StringComparison.Ordinal))
            return "In progress";
        return "Unknown";
    }
}

public sealed class ReportChoice : INotifyPropertyChanged
{
    public required ReportMetadata Report { get; init; }
    public required string Kind { get; init; }

    bool _isChecked;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public string Title => string.IsNullOrWhiteSpace(Report.Name) ? "(unnamed report)" : Report.Name!;

    public string Detail =>
        $"{Kind} · {Report.RaceName} · config {Report.ReportBuilderConfigId} · report {Report.ReportId} · rev {Report.Revision} · {Report.Fields?.Count ?? 0} fields";

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class ReportPayload
{
    public required string RawJson { get; init; }
    public required ReportData Data { get; init; }
}

public sealed class ResultManifest
{
    [JsonPropertyName("generatedAtUtc")]
    public string GeneratedAtUtc { get; set; } = "";

    [JsonPropertyName("sourceBaseUrl")]
    public string SourceBaseUrl { get; set; } = "";

    [JsonPropertyName("raceFile")]
    public string? RaceFile { get; set; }

    [JsonPropertyName("reportsMetaFile")]
    public string? ReportsMetaFile { get; set; }

    [JsonPropertyName("raceName")]
    public string? RaceName { get; set; }

    [JsonPropertyName("rdgoRaceId")]
    public int? RdgoRaceId { get; set; }

    [JsonPropertyName("uploadStatus")]
    public string UploadStatus { get; set; } = "local-only";

    [JsonPropertyName("reports")]
    public List<ManifestReport> Reports { get; set; } = new();
}

public sealed class ManifestReport
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("raceName")]
    public string? RaceName { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("reportId")]
    public int ReportId { get; set; }

    [JsonPropertyName("reportBuilderConfigId")]
    public int ReportBuilderConfigId { get; set; }

    [JsonPropertyName("revision")]
    public int Revision { get; set; }

    [JsonPropertyName("rowCount")]
    public int RowCount { get; set; }

    [JsonPropertyName("csvFile")]
    public string CsvFile { get; set; } = "";

    [JsonPropertyName("jsonFile")]
    public string JsonFile { get; set; } = "";

    [JsonPropertyName("fields")]
    public List<ManifestField> Fields { get; set; } = new();
}

public sealed class ManifestField
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("label")]
    public string? Label { get; set; }

    [JsonPropertyName("columnHeader")]
    public string? ColumnHeader { get; set; }

    [JsonPropertyName("columnId")]
    public string? ColumnId { get; set; }
}

public sealed class SavedReport
{
    public required ReportChoice Choice { get; init; }
    public required ReportPayload Payload { get; init; }
}

public sealed class SaveRequest
{
    public required string OutputRoot { get; init; }
    public required string SourceBaseUrl { get; init; }
    public string? RaceName { get; init; }
    public int? RdgoRaceId { get; init; }
    public string? RaceRawJson { get; init; }
    public string? ReportsMetaRawJson { get; init; }
    public required IReadOnlyList<SavedReport> Reports { get; init; }
}

public sealed class SaveResult
{
    public required string DirectoryPath { get; init; }
    public required int ReportCount { get; init; }
    public required int RowCount { get; init; }
}

public sealed class AppSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 8080;
    public string Password { get; set; } = "12345";
    public string OutputFolder { get; set; } = "";
    public int RefreshSeconds { get; set; } = 15;
    public bool Automatic { get; set; } = true;
    public string Theme { get; set; } = AppThemes.Dark;
    public int ListenPort { get; set; } = 9002;
    public string ListenPassword { get; set; } = "";
    public string ListenLocation { get; set; } = "";
    public List<string>? SelectedLocations { get; set; }
    public bool Listen { get; set; } = true;
    public bool? SendReads { get; set; }
    public string JumbotronHost { get; set; } = "127.0.0.1";
    public int SendPort { get; set; } = 9100;
    public string JumbotronMode { get; set; } = JumbotronModes.BibReads;
    public string RaceStartTime { get; set; } = "";
    public string WeatherLocation { get; set; } = WeatherFeed.DefaultPlace;
    public string EventWelcomeImage { get; set; } = "";
    public string EventWelcomeHint { get; set; } = "";
    public string EventWelcomeUrl { get; set; } = "";
    public string CameraIp { get; set; } = "";
    public string CameraTemplate { get; set; } = CameraUrl.DefaultTemplate;
    public bool CameraAdvanced { get; set; }
    public bool CameraUseExact { get; set; }
    public string CameraExactUrl { get; set; } = "";
    public List<ScrollReportSetting> ScrollReports { get; set; } = new();
}

public sealed class LocationChoice : INotifyPropertyChanged
{
    public int Id { get; init; }
    public string Name { get; init; } = "";

    bool _isChecked = true;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class BibRead : INotifyPropertyChanged
{
    readonly BibHit _hit;

    public BibRead(BibHit hit) => _hit = hit;

    public int Id { get; init; }

    public string ReadTime => _hit.ReadTime;
    public string Location => _hit.Location;
    public string Message => _hit.Message;
    public string Bib => Prefer(_bib, _hit.Bib);
    public string Chip => Prefer(_chip, _hit.Chip);
    public string Name => Prefer(_name, _hit.Name);
    public string City => Prefer(_city, _hit.City);
    public string State => Prefer(_state, _hit.State);
    public string EventName => Prefer(_eventName, _hit.EventName);
    public string AgeGroup => Prefer(_ageGroup, _hit.AgeGroup);
    public string Gender => Prefer(_gender, _hit.Gender);
    public string Age => Prefer(_age, _hit.Age);
    public string Team => Prefer(_team, _hit.Team);
    public string Place => _place;
    public string ClockTime => _clockTime;
    public string FinishTime => _finishTime;
    public string Pace => _pace;
    public string Division => _division;
    public string DivisionPlace => _divisionPlace;
    public string ReportName => _reportName;

    string _bib = "";
    string _chip = "";
    string _name = "";
    string _city = "";
    string _state = "";
    string _eventName = "";
    string _ageGroup = "";
    string _gender = "";
    string _age = "";
    string _team = "";
    string _place = "";
    string _clockTime = "";
    string _finishTime = "";
    string _pace = "";
    string _division = "";
    string _divisionPlace = "";
    string _reportName = "Not in results";

    public void Match(AthleteRow? row)
    {
        _bib = row?.Bib ?? "";
        _chip = row?.Chip ?? "";
        _name = row?.Name ?? "";
        _city = row?.City ?? "";
        _state = row?.State ?? "";
        _eventName = row?.Event ?? "";
        _ageGroup = row?.AgeGroup ?? "";
        _gender = row?.Gender ?? "";
        _age = row?.Age ?? "";
        _team = "";
        _place = row?.Place ?? "";
        _clockTime = row?.ClockTime ?? "";
        _finishTime = row?.ChipTime ?? "";
        if (_finishTime.Length == 0)
            _finishTime = row?.ClockTime ?? "";
        _pace = row?.Pace ?? "";
        _division = row?.Division ?? "";
        _divisionPlace = row?.DivisionPlace ?? "";
        _reportName = row == null ? "Not in results" : row.ReportName;
        OnPropertyChanged(nameof(Bib));
        OnPropertyChanged(nameof(Chip));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(City));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(EventName));
        OnPropertyChanged(nameof(AgeGroup));
        OnPropertyChanged(nameof(Gender));
        OnPropertyChanged(nameof(Age));
        OnPropertyChanged(nameof(Team));
        OnPropertyChanged(nameof(Place));
        OnPropertyChanged(nameof(ClockTime));
        OnPropertyChanged(nameof(FinishTime));
        OnPropertyChanged(nameof(Pace));
        OnPropertyChanged(nameof(Division));
        OnPropertyChanged(nameof(DivisionPlace));
        OnPropertyChanged(nameof(ReportName));
    }

    public bool Rematch(ResultIndex index)
    {
        var before = WireText();
        Match(index.Find(_hit.Bib, _hit.Chip));
        return !string.Equals(before, WireText(), StringComparison.Ordinal);
    }

    string WireText() => string.Join('\u001f',
        Bib, Chip, Name, City, State, EventName, Place, FinishTime, ClockTime,
        Pace, Division, DivisionPlace, AgeGroup, Gender, Age, Team, ReportName, Message, Location, ReadTime);

    static string Prefer(string? preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class ReportSection : INotifyPropertyChanged
{
    public ReportMetadata Report { get; private set; } = new();
    public string Kind { get; private set; } = "Unknown";
    public ReportPayload? Payload { get; private set; }
    public string Signature { get; private set; } = "";

    string _title = "";
    System.Windows.Media.Geometry _mark = Marks.Runner;
    string _summary = "";
    double _titleTextSize = 15;
    double _detailTextSize = 12;
    double _boardTextSize = 13;
    string _headline = "Waiting for results";
    string _badge = "WAITING";
    System.Windows.Media.Brush _accent = System.Windows.Media.Brushes.SteelBlue;
    System.Windows.Media.Brush _badgeBrush = System.Windows.Media.Brushes.Gray;
    System.Data.DataView? _rows;

    static readonly System.Windows.Media.Brush[] AccentBrushes = CreateAccents();

    public string Title
    {
        get => _title;
        private set
        {
            if (_title == value)
                return;
            _title = value;
            var mark = value.Contains("Volunteer", StringComparison.OrdinalIgnoreCase) ? Marks.Mountain : Marks.Runner;
            if (!ReferenceEquals(_mark, mark))
            {
                _mark = mark;
                OnPropertyChanged(nameof(Mark));
            }
            OnPropertyChanged();
        }
    }

    public System.Windows.Media.Geometry Mark => _mark;

    public string Summary
    {
        get => _summary;
        private set
        {
            if (_summary == value)
                return;
            _summary = value;
            OnPropertyChanged();
        }
    }

    public double TitleTextSize
    {
        get => _titleTextSize;
        private set
        {
            if (_titleTextSize == value)
                return;
            _titleTextSize = value;
            OnPropertyChanged();
        }
    }

    public double DetailTextSize
    {
        get => _detailTextSize;
        private set
        {
            if (_detailTextSize == value)
                return;
            _detailTextSize = value;
            OnPropertyChanged();
        }
    }

    public double BoardTextSize
    {
        get => _boardTextSize;
        private set
        {
            if (_boardTextSize == value)
                return;
            _boardTextSize = value;
            OnPropertyChanged();
        }
    }

    public void UseBoardType(double boardSize)
    {
        BoardTextSize = boardSize;
        TitleTextSize = boardSize + 2;
        DetailTextSize = Math.Max(10, boardSize - 1);
    }

    public string Headline
    {
        get => _headline;
        private set
        {
            if (_headline == value)
                return;
            _headline = value;
            OnPropertyChanged();
        }
    }

    public string Badge
    {
        get => _badge;
        private set
        {
            if (_badge == value)
                return;
            _badge = value;
            OnPropertyChanged();
        }
    }

    public System.Windows.Media.Brush Accent => _accent;

    public System.Windows.Media.Brush BadgeBrush => _badgeBrush;

    public void PaintChrome(int index, System.Windows.Media.Brush live, System.Windows.Media.Brush waiting)
    {
        var accent = AccentBrushes[Math.Abs(index) % AccentBrushes.Length];
        if (!ReferenceEquals(_accent, accent))
        {
            _accent = accent;
            OnPropertyChanged(nameof(Accent));
        }

        var badge = Badge == "LIVE" ? live : waiting;
        if (ReferenceEquals(_badgeBrush, badge))
            return;
        _badgeBrush = badge;
        OnPropertyChanged(nameof(BadgeBrush));
    }

    static System.Windows.Media.Brush[] CreateAccents()
    {
        var colors = new[]
        {
            System.Windows.Media.Color.FromRgb(0x2F, 0x7B, 0xFF),
            System.Windows.Media.Color.FromRgb(0xA8, 0x55, 0xF7),
            System.Windows.Media.Color.FromRgb(0x22, 0xC5, 0x5E),
            System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B)
        };
        var brushes = new System.Windows.Media.Brush[colors.Length];
        for (var i = 0; i < colors.Length; i++)
        {
            var brush = new System.Windows.Media.SolidColorBrush(colors[i]);
            brush.Freeze();
            brushes[i] = brush;
        }
        return brushes;
    }

    public System.Data.DataView? Rows
    {
        get => _rows;
        private set
        {
            _rows = value;
            OnPropertyChanged();
        }
    }

    public bool Apply(ReportMetadata report, ReportPayload payload)
    {
        var signature = SignatureOf(report, payload.Data);
        if (signature == Signature && Signature.Length > 0)
            return false;

        var kind = ReportKinds.FromName(report.Name);
        Report = report;
        Kind = kind;
        Payload = payload;
        Signature = signature;
        Title = string.IsNullOrWhiteSpace(report.Name) ? "(unnamed report)" : report.Name!;
        var rows = payload.Data.Results?.Count ?? 0;
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        Headline = rows == 1 ? "1 finisher" : $"{rows} finishers";
        Badge = rows > 0 ? "LIVE" : "WAITING";
        Summary = rows == 0
            ? "No finishers yet"
            : $"{rows} results · updated {stamp}";
        Rows = LocalResultWriter.BuildTable(report, payload.Data);
        return true;
    }

    static string SignatureOf(ReportMetadata report, ReportData data)
    {
        var fields = report.Fields ?? new();
        var rows = data.Results ?? new();
        var builder = new System.Text.StringBuilder(256 + rows.Count * 64);
        builder.Append(report.ReportBuilderConfigId);
        builder.Append('\n');
        foreach (var field in fields)
        {
            builder.Append(field.ColumnId);
            builder.Append('\u001f');
            builder.Append(field.Name);
            builder.Append('\u001e');
        }

        foreach (var row in rows)
        {
            builder.Append('\n');
            for (var i = 0; i < row.Count; i++)
            {
                if (i > 0)
                    builder.Append('\u001f');
                builder.Append(LocalResultWriter.FormatCell(row[i]));
            }
        }

        return builder.ToString();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
