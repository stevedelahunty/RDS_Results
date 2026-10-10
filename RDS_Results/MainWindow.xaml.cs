using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace RDS_Results;

public partial class MainWindow : Window
{
    readonly ObservableCollection<ReportSection> _sections = new();
    readonly ObservableCollection<BibRead> _reads = new();
    readonly ObservableCollection<LocationChoice> _locations = new();
    readonly LiveSocket _liveSocket = new();
    readonly RaceDayClient _client = new();
    readonly BibSender _bibSender = new();
    readonly SemaphoreSlim _pollGate = new(1, 1);
    readonly CancellationTokenSource _pollCts = new();

    Task? _pollTask;
    Task? _listenTask;
    CancellationTokenSource? _listenCts;
    ResultIndex _index = ResultIndex.Build(Array.Empty<ReportSection>());
    string? _raceRawJson;
    string? _reportsMetaRawJson;
    RaceMetadata? _race;
    string? _lastSavedFolder;
    string _theme = AppThemes.Dark;
    volatile bool _automatic = true;
    volatile int _refreshSeconds = 15;
    DateTime? _lastPoll;
    volatile bool _closing;
    bool _haveSnapshot;
    AppView _view = AppView.Results;
    bool _suppressLocationChange;
    bool _networkStarted;
    bool _suppressSettings;
    int _nextReadId = 1;
    List<string>? _savedLocationNames;
    readonly AppSettings _settings = new();
    readonly List<string> _pendingLog = new();
    SettingsPanel? _settingsUi;
    DataGrid? _readsGrid;
    bool _gridChrome;
    string _sendStatus = "Bib reads stay on this computer until sending starts.";
    int _displayCount;
    DispatcherTimer? _clock;

    public MainWindow()
    {
        LoadSettings();
        AppThemes.Apply(_theme);
        InitializeComponent();
        ReportSections.ItemsSource = _sections;
        LocationChoices.ItemsSource = _locations;
        PaintViewButtons();
        PaintLive();
        StartClock();
        FitToWorkArea();
        Log("Watching RaceDay Scoring. Reports show up here after they are made available via the kiosk.");
    }

    void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        if (MinWidth > area.Width)
            MinWidth = area.Width;
        if (MinHeight > area.Height)
            MinHeight = area.Height;
        if (Width > area.Width)
            Width = area.Width;
        if (Height > area.Height)
            Height = area.Height;
        Left = area.Left + Math.Max(0, (area.Width - Width) / 2);
        Top = area.Top + Math.Max(0, (area.Height - Height) / 2);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_networkStarted)
            return;
        _networkStarted = true;
        StartSending();
        _pollTask = Task.Factory.StartNew(
            () => PollLoopAsync(_pollCts.Token),
            _pollCts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
        if (_settings.Listen)
            StartListening();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _clock?.Stop();
        SaveSettings();
        _pollCts.Cancel();
        StopListening();
        _bibSender.Dispose();
        try
        {
            _pollTask?.Wait(TimeSpan.FromSeconds(3));
            _listenTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }
        _listenCts?.Dispose();
        _pollCts.Dispose();
        _client.Dispose();
        _pollGate.Dispose();
        base.OnClosed(e);
    }

    internal async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await PollOnceAsync(_pollCts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log(ex.Message);
        }
    }

    internal async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveDisplayedAsync();
    }

    internal void IntervalBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitInterval();
        SaveSettings();
    }

    internal void AutoRefreshBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSettings || _settingsUi == null)
            return;
        _automatic = _settingsUi.AutoRefreshBox.IsChecked == true;
        _settings.Automatic = _automatic;
        PaintLive();
        SaveSettings();
    }

    internal void LightThemeButton_Click(object sender, RoutedEventArgs e) => SetTheme(AppThemes.Light);

    internal void DarkThemeButton_Click(object sender, RoutedEventArgs e) => SetTheme(AppThemes.Dark);

    internal void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var current = _settingsUi?.OutputFolderBox.Text ?? _settings.OutputFolder;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Folder for local result lists",
            InitialDirectory = Directory.Exists(current) ? current : DefaultOutputFolder()
        };
        if (dialog.ShowDialog(this) == true)
        {
            _settings.OutputFolder = dialog.FolderName;
            if (_settingsUi != null)
                _settingsUi.OutputFolderBox.Text = dialog.FolderName;
            SaveSettings();
        }
    }

    internal void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastSavedFolder) || !Directory.Exists(_lastSavedFolder))
            return;
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = _lastSavedFolder,
            UseShellExecute = true
        });
    }

    async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_automatic)
                    await PollOnceAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                await ReportAsync("Refresh failed", ex.Message);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(_refreshSeconds, 2, 3600)), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        if (!await _pollGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            await ReportAsync("Refresh already running", null);
            return;
        }

        try
        {
            Uri baseUri;
            string password;
            try
            {
                (baseUri, password) = await Dispatcher.InvokeAsync(ReadConnection);
            }
            catch (Exception ex)
            {
                await ReportAsync(ex.Message, ex.Message);
                return;
            }

            await ReportAsync("Refreshing...", null);
            cancellationToken.ThrowIfCancellationRequested();

            string? raceRaw = null;
            RaceMetadata? race = null;
            try
            {
                raceRaw = await _client.GetRawAsync(baseUri, password, "/race/meta", cancellationToken).ConfigureAwait(false);
                race = RaceDayClient.ParseRace(raceRaw);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                await ReportAsync("Refreshing...", "GET /race/meta failed. Report list will still load. " + ex.Message);
                await UpdateHubBannerAsync(ex);
            }

            var metaRaw = await _client.GetRawAsync(baseUri, password, "/results/meta", cancellationToken).ConfigureAwait(false);
            var meta = RaceDayClient.ParseReports(metaRaw);
            var downloaded = new List<DownloadedReport>(meta.Reports.Count);
            foreach (var report in meta.Reports)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = "/results/report/" + report.ReportBuilderConfigId;
                var raw = await _client.GetRawAsync(baseUri, password, path, cancellationToken).ConfigureAwait(false);
                downloaded.Add(new DownloadedReport(report, new ReportPayload
                {
                    RawJson = raw,
                    Data = RaceDayClient.ParseReport(raw)
                }));
            }

            await Dispatcher.InvokeAsync(() => ApplySnapshot(race, raceRaw, metaRaw, downloaded));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await ReportAsync("Refresh failed", ex.Message);
            await UpdateHubBannerAsync(ex);
        }
        finally
        {
            _pollGate.Release();
        }
    }

    void ApplySnapshot(RaceMetadata? race, string? raceRaw, string reportsMetaRaw, IReadOnlyList<DownloadedReport> downloaded)
    {
        if (_closing)
            return;

        EnsureGridChrome();

        _race = race;
        _raceRawJson = raceRaw;
        _reportsMetaRawJson = reportsMetaRaw;
        ShowRace(race);

        var incoming = new Dictionary<int, DownloadedReport>();
        foreach (var item in downloaded)
            incoming[item.Report.ReportBuilderConfigId] = item;

        var removed = new List<string>();
        for (var i = _sections.Count - 1; i >= 0; i--)
        {
            if (incoming.ContainsKey(_sections[i].Report.ReportBuilderConfigId))
                continue;
            removed.Add(_sections[i].Title);
            _sections.RemoveAt(i);
        }

        var added = new List<ReportSection>();
        var existing = _sections.ToDictionary(section => section.Report.ReportBuilderConfigId);
        foreach (var item in downloaded)
        {
            var id = item.Report.ReportBuilderConfigId;
            if (!existing.TryGetValue(id, out var section))
            {
                section = new ReportSection();
                section.Apply(item.Report, item.Payload);
                _sections.Add(section);
                added.Add(section);
                continue;
            }

            var scroller = CaptureGridScroll(section);
            var rowsChanged = section.Apply(item.Report, item.Payload);
            if (rowsChanged)
                RestoreGridScroll(scroller);
        }

        EmptyStateText.Visibility = _sections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LayoutBoard();
        RematchReads();
        _lastPoll = DateTime.Now;
        PaintLink(true, _automatic
            ? "Last poll"
            : "Last poll");
        PaintStats();
        ShowHubBanner(false);

        if (_haveSnapshot && (added.Count > 0 || removed.Count > 0))
        {
            if (added.Count > 0)
            {
                ReportSections.UpdateLayout();
                if (ReportSections.ItemContainerGenerator.ContainerFromItem(added[0]) is FrameworkElement element)
                    element.BringIntoView();
            }
            AnnounceReportChanges(added.Select(section => section.Title).ToList(), removed);
        }
        else if (!_haveSnapshot && _sections.Count > 0)
        {
            Log($"Loaded {_sections.Count} report(s).");
        }

        _haveSnapshot = true;
    }

    void BoardScroller_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutBoard();

    void LayoutBoard()
    {
        var count = _sections.Count;
        if (count == 0)
            return;

        var width = BoardHost.ActualWidth;
        var height = BoardHost.ActualHeight;
        if (width < 1 || height < 1)
            return;

        var columns = BoardFit.Across(count, width);
        if (FindVisualChild<UniformGrid>(ReportSections) is UniformGrid panel)
            panel.Columns = columns;

        var panelWidth = width / columns;
        var typeSize = BoardFit.FontSize(panelWidth);
        var live = (Brush)FindResource("GoodBrush");
        var waiting = (Brush)FindResource("WarnBrush");
        for (var i = 0; i < _sections.Count; i++)
        {
            _sections[i].UseBoardType(typeSize);
            _sections[i].PaintChrome(i, live, waiting);
        }

        ReportSections.UpdateLayout();

        for (var i = 0; i < count; i++)
        {
            if (ReportSections.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement element)
                continue;
            element.Height = double.NaN;
            var columnWidth = element.ActualWidth > 1 ? element.ActualWidth : panelWidth;
            if (FindVisualChild<DataGrid>(element) is DataGrid grid)
                BoardFit.Apply(grid, columnWidth);
        }
    }

    void AnnounceReportChanges(IReadOnlyList<string> added, IReadOnlyList<string> removed)
    {
        var lines = new List<string>();
        if (added.Count > 0)
        {
            lines.Add(added.Count == 1 ? "A new report is available:" : "New reports are available:");
            lines.AddRange(added.Select(name => "• " + name));
            Log(string.Join(" ", added.Select(name => "Available: " + name)));
        }
        if (removed.Count > 0)
        {
            if (lines.Count > 0)
                lines.Add("");
            lines.Add(removed.Count == 1 ? "A report is no longer available:" : "These reports are no longer available:");
            lines.AddRange(removed.Select(name => "• " + name));
            Log(string.Join(" ", removed.Select(name => "No longer available: " + name)));
        }

        MessageBox.Show(
            this,
            string.Join(Environment.NewLine, lines),
            "RaceDay Results",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    ScrollViewer? CaptureGridScroll(ReportSection section)
    {
        if (ReportSections.ItemContainerGenerator.ContainerFromItem(section) is not DependencyObject container)
            return null;
        var grid = FindVisualChild<DataGrid>(container);
        return grid == null ? null : FindVisualChild<ScrollViewer>(grid);
    }

    void RestoreGridScroll(ScrollViewer? scroller)
    {
        if (scroller == null)
            return;
        var vertical = scroller.VerticalOffset;
        var horizontal = scroller.HorizontalOffset;
        Dispatcher.BeginInvoke(() =>
        {
            scroller.ScrollToVerticalOffset(vertical);
            scroller.ScrollToHorizontalOffset(horizontal);
        }, DispatcherPriority.Loaded);
    }

    async Task SaveDisplayedAsync()
    {
        var snapshot = _sections
            .Where(section => section.Payload != null)
            .Select(section => new SavedReport
            {
                Choice = new ReportChoice
                {
                    Report = section.Report,
                    Kind = section.Kind
                },
                Payload = section.Payload!
            })
            .ToList();
        if (snapshot.Count == 0)
        {
            StatusText.Text = "No reports to save";
            Log("No reports are on screen.");
            return;
        }

        Uri baseUri;
        try
        {
            baseUri = ReadBaseUri();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            Log(ex.Message);
            return;
        }

        PullSettingsFromUi();
        var outputRoot = _settings.OutputFolder;
        if (outputRoot.Length == 0)
        {
            StatusText.Text = "Choose an output folder";
            return;
        }

        if (_settingsUi != null)
            _settingsUi.SaveButton.IsEnabled = false;
        StatusText.Text = "Saving...";
        try
        {
            var race = _race?.Race;
            var raceRaw = _raceRawJson;
            var metaRaw = _reportsMetaRawJson;
            var result = await Task.Run(() => LocalResultWriter.Write(new SaveRequest
            {
                OutputRoot = outputRoot,
                SourceBaseUrl = baseUri.ToString().TrimEnd('/'),
                RaceName = race?.Name,
                RdgoRaceId = race?.RdgoRaceId,
                RaceRawJson = raceRaw,
                ReportsMetaRawJson = metaRaw,
                Reports = snapshot
            }));

            _lastSavedFolder = result.DirectoryPath;
            if (_settingsUi != null)
                _settingsUi.OpenFolderButton.IsEnabled = true;
            StatusText.Text = $"Saved {result.ReportCount} lists, {result.RowCount} rows";
            Log($"Wrote {result.DirectoryPath}");
            SaveSettings();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Save failed";
            Log(ex.Message);
        }
        finally
        {
            if (_settingsUi != null)
                _settingsUi.SaveButton.IsEnabled = true;
        }
    }

    void ShowRace(RaceMetadata? race)
    {
        if (race?.Race == null)
        {
            RaceTitleText.Text = "Waiting for RaceDay";
            RaceMetaText.Text = "Event ID loads with the race.";
            DateStartText.Text = "—";
            DateEndText.Text = "";
            EventsText.Text = "Distance loads with the race.";
            return;
        }

        var info = race.Race;
        var integration = info.IntegrationType == 1 ? "RunSignup" : "Local";
        var eventId = string.IsNullOrWhiteSpace(info.IntegrationUniqueKey)
            ? info.RdgoRaceId.ToString()
            : info.IntegrationUniqueKey;
        RaceTitleText.Text = string.IsNullOrWhiteSpace(info.Name) ? "Race" : info.Name;
        RaceMetaText.Text = $"{integration} Event ID {eventId}";
        DateStartText.Text = FormatRaceDay(info.StartDate);
        DateEndText.Text = FormatRaceDay(info.EndDate);
        EventsText.Text = DescribeCourse(race.Events);
    }

    static string FormatRaceDay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "—";
        return DateTime.TryParse(value, out var day) ? day.ToString("MMM d, yyyy") : value;
    }

    static string DescribeCourse(List<RaceEvent>? events)
    {
        var listed = events ?? new();
        var distances = listed
            .Select(item => item.Distance)
            .Where(distance => !string.IsNullOrWhiteSpace(distance))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distances.Count == 0)
            return listed.Count > 1 ? "Multiple divisions" : "Distance not set";
        if (listed.Count > 1)
            return $"{distances[0]} (multiple divisions)";
        return distances[0]!;
    }

    (Uri BaseUri, string Password) ReadConnection()
    {
        PullSettingsFromUi();
        return (ReadBaseUri(), _settings.Password);
    }

    Uri ReadBaseUri()
    {
        if (_settingsUi != null)
        {
            if (!int.TryParse(_settingsUi.PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
                throw new InvalidOperationException("Port must be a number.");
            _settings.Port = port;
            _settings.Host = _settingsUi.HostBox.Text.Trim();
        }
        if (_settings.Port is < 1 or > 65535)
            throw new InvalidOperationException("Port must be a number.");
        return RaceDayClient.BuildBaseUri(_settings.Host, _settings.Port);
    }

    void CommitInterval()
    {
        var seconds = _refreshSeconds;
        if (_settingsUi != null && int.TryParse(_settingsUi.IntervalBox.Text.Trim(), out var parsed))
            seconds = parsed;
        seconds = Math.Clamp(seconds, 2, 3600);
        _refreshSeconds = seconds;
        _settings.RefreshSeconds = seconds;
        if (_settingsUi != null)
            _settingsUi.IntervalBox.Text = seconds.ToString();
    }

    void ResultsViewButton_Click(object sender, RoutedEventArgs e) => ShowView(AppView.Results);

    void ReadsViewButton_Click(object sender, RoutedEventArgs e) => ShowView(AppView.Reads);

    void SettingsViewButton_Click(object sender, RoutedEventArgs e) => ShowView(AppView.Settings);

    void ShowView(AppView view)
    {
        _view = view;
        if (view == AppView.Reads)
            EnsureReadsGrid();
        if (view == AppView.Settings)
            EnsureSettings();
        ResultsBoard.Visibility = view == AppView.Results ? Visibility.Visible : Visibility.Collapsed;
        ReadsBoard.Visibility = view == AppView.Reads ? Visibility.Visible : Visibility.Collapsed;
        SettingsBoard.Visibility = view == AppView.Settings ? Visibility.Visible : Visibility.Collapsed;
        PaintViewButtons();
    }

    void PaintViewButtons()
    {
        var onBoard = _view == AppView.Results;
        ResultsViewButton.Background = (Brush)FindResource(onBoard ? "GoldBrush" : "PanelBrush");
        ResultsViewButton.Foreground = (Brush)FindResource(onBoard ? "GoldInkBrush" : "InkBrush");
        ResultsViewButton.BorderBrush = (Brush)FindResource(onBoard ? "GoldBrush" : "LineBrush");
        ResultsViewButton.FontWeight = onBoard ? FontWeights.SemiBold : FontWeights.Normal;
        PaintThemeButton(ReadsViewButton, _view == AppView.Reads);
        PaintThemeButton(SettingsViewButton, _view == AppView.Settings);
    }

    internal void HostBox_LostFocus(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        if (_settings.Listen)
            StartListening();
    }

    internal void ListenSettings_LostFocus(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        if (_settings.Listen)
            StartListening();
    }

    internal void ListenBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSettings || _settingsUi == null)
            return;
        PullSettingsFromUi();
        if (_settings.Listen)
            StartListening();
        else
            StopListening();
        SaveSettings();
    }

    internal void SendReadsBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSettings || _settingsUi == null)
            return;
        PullSettingsFromUi();
        SaveSettings();
        StartSending();
    }

    internal void SendPortBox_LostFocus(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        StartSending();
    }

    void StartSending()
    {
        if (!(_settings.SendReads ?? true))
        {
            _bibSender.Stop();
            SetSendStatus("Bib reads stay on this computer.");
            return;
        }

        if (_settingsUi != null)
        {
            if (!int.TryParse(_settingsUi.SendPortBox.Text.Trim(), out var typed) || typed is < 1 or > 65535)
            {
                _bibSender.Stop();
                SetSendStatus("Display port must be a number from 1 to 65535.");
                return;
            }
            _settings.SendPort = typed;
        }

        if (_settings.SendPort is < 1 or > 65535)
        {
            _bibSender.Stop();
            SetSendStatus("Display port must be a number from 1 to 65535.");
            return;
        }

        var port = _settings.SendPort;

        if (!_bibSender.Start(port, ShowSendStatus))
            return;

        foreach (var read in _reads.Reverse())
            _bibSender.Publish(read, false);
    }

    void PublishRead(BibRead read, bool update)
    {
        if (!(_settings.SendReads ?? true))
            return;
        _bibSender.Publish(read, update);
    }

    void ShowSendStatus(string status)
    {
        if (_closing)
            return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing)
                return;
            SetSendStatus(status);
            Log(status);
        });
    }

    void StartListening()
    {
        StopListening();
        ListenSettings settings;
        try
        {
            settings = ReadListenSettings();
        }
        catch (Exception ex)
        {
            ReadStatusText.Text = ex.Message;
            Log(ex.Message);
            return;
        }

        _listenCts = CancellationTokenSource.CreateLinkedTokenSource(_pollCts.Token);
        var token = _listenCts.Token;
        _listenTask = Task.Run(() => ReadListener.RunAsync(settings, OnReadAsync, OnListenStatusAsync, OnLocationsAsync, _liveSocket, token));
    }

    void StopListening()
    {
        try
        {
            _listenCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        if (!_closing)
            ReadStatusText.Text = "Not listening";
    }

    ListenSettings ReadListenSettings()
    {
        PullSettingsFromUi();
        if (_settingsUi != null && !int.TryParse(_settingsUi.ListenPortBox.Text.Trim(), out _))
            throw new InvalidOperationException("Listen port must be a number.");
        if (_settings.ListenPort is < 1 or > 65535)
            throw new InvalidOperationException("Listen port must be a number.");
        return new ListenSettings(_settings.Host, _settings.ListenPort, _settings.ListenPassword);
    }

    Task<IReadOnlyList<int>> OnLocationsAsync(IReadOnlyList<AnnouncerLocation> offered)
    {
        if (_closing)
            return Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
        try
        {
            return Dispatcher.InvokeAsync(() => ApplyLocations(offered)).Task;
        }
        catch (Exception)
        {
            return Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
        }
    }

    IReadOnlyList<int> ApplyLocations(IReadOnlyList<AnnouncerLocation> offered)
    {
        _suppressLocationChange = true;
        foreach (var existing in _locations)
            existing.PropertyChanged -= LocationChoice_PropertyChanged;
        _locations.Clear();
        foreach (var location in offered)
        {
            var choice = new LocationChoice
            {
                Id = location.Id,
                Name = location.Name,
                IsChecked = _savedLocationNames == null || _savedLocationNames.Contains(location.Name, StringComparer.OrdinalIgnoreCase)
            };
            choice.PropertyChanged += LocationChoice_PropertyChanged;
            _locations.Add(choice);
        }
        _suppressLocationChange = false;
        LocationEmptyText.Visibility = _locations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var selected = SelectedLocationIds();
        DescribeLocations();
        return selected;
    }

    void LocationChoice_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressLocationChange || e.PropertyName != nameof(LocationChoice.IsChecked))
            return;
        var selected = SelectedLocationIds();
        _savedLocationNames = _locations.Where(location => location.IsChecked).Select(location => location.Name).ToList();
        DescribeLocations();
        SaveSettings();
        _ = ReadListener.SendLocationSelectionAsync(_liveSocket, selected, OnListenStatusAsync, _pollCts.Token);
    }

    List<int> SelectedLocationIds() =>
        _locations.Where(location => location.IsChecked).Select(location => location.Id).ToList();

    void DescribeLocations()
    {
        if (_locations.Count == 0)
            return;
        var names = _locations.Where(location => location.IsChecked).Select(location => location.Name).ToList();
        ReadStatusText.Text = names.Count == 0
            ? "No locations are checked."
            : "Listening at " + string.Join(", ", names) + ".";
    }

    Task OnReadAsync(BibHit hit)
    {
        if (_closing)
            return Task.CompletedTask;
        try
        {
            return Dispatcher.InvokeAsync(() =>
            {
                if (_closing)
                    return;
                var record = new BibRead(hit) { Id = _nextReadId++ };
                record.Match(_index.Find(hit.Bib, hit.Chip));
                _reads.Insert(0, record);
                PublishRead(record, false);
                while (_reads.Count > 400)
                    _reads.RemoveAt(_reads.Count - 1);
                var finish = record.FinishTime.Length == 0 ? "no finish time" : "finish " + record.FinishTime;
                Log($"Read bib {record.Bib} · chip {record.Chip} · {record.Name} · {finish} · {record.ReportName}");
            }).Task;
        }
        catch (Exception)
        {
            return Task.CompletedTask;
        }
    }

    Task OnListenStatusAsync(string status)
    {
        if (_closing)
            return Task.CompletedTask;
        try
        {
            return Dispatcher.InvokeAsync(() =>
            {
                if (_closing)
                    return;
                ReadStatusText.Text = status;
                Log(status);
            }).Task;
        }
        catch (Exception)
        {
            return Task.CompletedTask;
        }
    }

    void RematchReads()
    {
        _index = ResultIndex.Build(_sections);
        foreach (var read in _reads)
        {
            if (read.Rematch(_index))
                PublishRead(read, true);
        }
    }

    void SetTheme(string theme)
    {
        _theme = AppThemes.Normalize(theme);
        AppThemes.Apply(_theme);
        PaintThemeButtons();
        PaintViewButtons();
        PaintLive();
        PaintLink(ConnectionText.Text.StartsWith("Connected", StringComparison.Ordinal), StatusText.Text);
        for (var i = 0; i < _sections.Count; i++)
            _sections[i].PaintChrome(i, (Brush)FindResource("GoodBrush"), (Brush)FindResource("WarnBrush"));
        SaveSettings();
    }

    void PaintThemeButtons()
    {
        if (_settingsUi == null)
            return;
        PaintThemeButton(_settingsUi.LightThemeButton, _theme == AppThemes.Light);
        PaintThemeButton(_settingsUi.DarkThemeButton, _theme == AppThemes.Dark);
    }

    void PaintThemeButton(Button button, bool selected)
    {
        button.Background = (Brush)FindResource(selected ? "AccentBrush" : "PanelBrush");
        button.Foreground = (Brush)FindResource(selected ? "AccentInkBrush" : "InkBrush");
        button.BorderBrush = (Brush)FindResource(selected ? "AccentBrush" : "LineBrush");
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }

    Task ReportAsync(string status, string? log)
    {
        if (_closing)
            return Task.CompletedTask;
        try
        {
            return Dispatcher.InvokeAsync(() =>
            {
                if (_closing)
                    return;
                var failed = status.Contains("failed", StringComparison.OrdinalIgnoreCase);
                if (failed)
                    PaintLink(false, status);
                else
                    StatusText.Text = status;
                if (log != null)
                    Log(log);
            }).Task;
        }
        catch (Exception)
        {
            return Task.CompletedTask;
        }
    }

    void Log(string message)
    {
        var line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
        PushActivity(line);
        if (_settingsUi == null)
            return;

        var box = _settingsUi.LogBox;
        box.AppendText(line + Environment.NewLine);
        var lines = box.Text.Split(Environment.NewLine);
        if (lines.Length > 200)
            box.Text = string.Join(Environment.NewLine, lines.Skip(lines.Length - 150));
        box.CaretIndex = box.Text.Length;
        box.ScrollToEnd();
    }

    static string DefaultOutputFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "RDS_Results");

    void LoadSettings()
    {
        if (string.IsNullOrWhiteSpace(_settings.OutputFolder))
            _settings.OutputFolder = DefaultOutputFolder();
        try
        {
            var path = SettingsFile.Path;
            if (!File.Exists(path))
                return;
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
            if (settings == null)
                return;
            if (!string.IsNullOrWhiteSpace(settings.Host))
                _settings.Host = settings.Host;
            if (settings.Port is > 0 and <= 65535)
                _settings.Port = settings.Port;
            _settings.Password = settings.Password ?? "12345";
            if (!string.IsNullOrWhiteSpace(settings.OutputFolder))
                _settings.OutputFolder = settings.OutputFolder;
            _refreshSeconds = Math.Clamp(settings.RefreshSeconds <= 0 ? 15 : settings.RefreshSeconds, 2, 3600);
            _settings.RefreshSeconds = _refreshSeconds;
            _automatic = settings.Automatic;
            _settings.Automatic = _automatic;
            if (settings.ListenPort is > 0 and <= 65535)
                _settings.ListenPort = settings.ListenPort;
            _settings.ListenPassword = settings.ListenPassword ?? "";
            if (settings.SelectedLocations != null)
                _savedLocationNames = settings.SelectedLocations;
            else if (!string.IsNullOrWhiteSpace(settings.ListenLocation))
                _savedLocationNames = new List<string> { settings.ListenLocation.Trim() };
            _settings.Listen = settings.Listen;
            _settings.SendReads = settings.SendReads ?? true;
            if (settings.SendPort is > 0 and <= 65535)
                _settings.SendPort = settings.SendPort;
            _theme = AppThemes.Normalize(settings.Theme);
            _settings.Theme = _theme;
        }
        catch (Exception ex)
        {
            Log("Could not read saved settings. " + ex.Message);
        }
    }

    void SaveSettings()
    {
        try
        {
            CommitInterval();
            PullSettingsFromUi();
            _settings.Theme = _theme;
            _settings.RefreshSeconds = _refreshSeconds;
            _settings.Automatic = _automatic;
            _settings.SelectedLocations = _locations.Count > 0
                ? _locations.Where(location => location.IsChecked).Select(location => location.Name).ToList()
                : _savedLocationNames;
            if (string.IsNullOrWhiteSpace(_settings.OutputFolder))
                _settings.OutputFolder = DefaultOutputFolder();
            File.WriteAllText(SettingsFile.Path, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log("Could not save settings. " + ex.Message);
        }
    }

    void PullSettingsFromUi()
    {
        if (_settingsUi == null)
            return;
        var ui = _settingsUi;
        _settings.Host = ui.HostBox.Text.Trim();
        if (int.TryParse(ui.PortBox.Text.Trim(), out var port) && port is > 0 and <= 65535)
            _settings.Port = port;
        _settings.Password = ui.PasswordBox.Text;
        _settings.OutputFolder = ui.OutputFolderBox.Text.Trim();
        if (int.TryParse(ui.IntervalBox.Text.Trim(), out var seconds))
        {
            _refreshSeconds = Math.Clamp(seconds, 2, 3600);
            _settings.RefreshSeconds = _refreshSeconds;
        }
        _automatic = ui.AutoRefreshBox.IsChecked == true;
        _settings.Automatic = _automatic;
        _settings.ListenPassword = ui.ListenPasswordBox.Text;
        if (int.TryParse(ui.ListenPortBox.Text.Trim(), out var listenPort) && listenPort is > 0 and <= 65535)
            _settings.ListenPort = listenPort;
        _settings.Listen = ui.ListenBox.IsChecked == true;
        _settings.SendReads = ui.SendReadsBox.IsChecked == true;
        if (int.TryParse(ui.SendPortBox.Text.Trim(), out var sendPort) && sendPort is > 0 and <= 65535)
            _settings.SendPort = sendPort;
    }

    void EnsureSettings()
    {
        if (_settingsUi != null)
            return;
        _suppressSettings = true;
        var panel = new SettingsPanel { Host = this };
        ApplySettingsToUi(panel);
        _settingsUi = panel;
        SettingsBoard.Content = panel;
        if (_pendingLog.Count > 0)
        {
            panel.LogBox.Text = string.Join(Environment.NewLine, _pendingLog) + Environment.NewLine;
            panel.LogBox.CaretIndex = panel.LogBox.Text.Length;
            panel.LogBox.ScrollToEnd();
        }
        PaintThemeButtons();
        Dispatcher.BeginInvoke(() => _suppressSettings = false, DispatcherPriority.Loaded);
    }

    void ApplySettingsToUi(SettingsPanel ui)
    {
        ui.HostBox.Text = _settings.Host;
        ui.PortBox.Text = _settings.Port.ToString();
        ui.PasswordBox.Text = _settings.Password ?? "12345";
        ui.OutputFolderBox.Text = string.IsNullOrWhiteSpace(_settings.OutputFolder) ? DefaultOutputFolder() : _settings.OutputFolder;
        ui.IntervalBox.Text = _refreshSeconds.ToString();
        ui.AutoRefreshBox.IsChecked = _automatic;
        ui.ListenPortBox.Text = _settings.ListenPort.ToString();
        ui.ListenPasswordBox.Text = _settings.ListenPassword ?? "";
        ui.ListenBox.IsChecked = _settings.Listen;
        ui.SendReadsBox.IsChecked = _settings.SendReads ?? true;
        ui.SendPortBox.Text = _settings.SendPort.ToString();
        ui.SendStatusText.Text = _sendStatus;
    }

    void EnsureGridChrome()
    {
        if (_gridChrome)
            return;
        _gridChrome = true;
        var styles = new GridStyles();
        Application.Current.Resources.MergedDictionaries.Add(styles);
        ReportSections.ItemTemplate = (DataTemplate)styles["ReportSectionTemplate"];
    }

    void EnsureReadsGrid()
    {
        if (_readsGrid != null)
            return;
        EnsureGridChrome();
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            ItemsSource = _reads
        };
        AddReadColumn(grid, "Read", nameof(BibRead.ReadTime), 90);
        AddReadColumn(grid, "Location", nameof(BibRead.Location), 110);
        AddReadColumn(grid, "Bib", nameof(BibRead.Bib), 70);
        AddReadColumn(grid, "Chip", nameof(BibRead.Chip), 80);
        AddReadColumn(grid, "Name", nameof(BibRead.Name), 180);
        AddReadColumn(grid, "City", nameof(BibRead.City), 120);
        AddReadColumn(grid, "State", nameof(BibRead.State), 60);
        AddReadColumn(grid, "Event", nameof(BibRead.EventName), 140);
        AddReadColumn(grid, "Place", nameof(BibRead.Place), 70);
        AddReadColumn(grid, "Finish", nameof(BibRead.FinishTime), 100);
        AddReadColumn(grid, "Clock", nameof(BibRead.ClockTime), 100);
        AddReadColumn(grid, "Pace", nameof(BibRead.Pace), 80);
        AddReadColumn(grid, "Division", nameof(BibRead.Division), 120);
        AddReadColumn(grid, "Div place", nameof(BibRead.DivisionPlace), 90);
        AddReadColumn(grid, "Age group", nameof(BibRead.AgeGroup), 110);
        AddReadColumn(grid, "Gender", nameof(BibRead.Gender), 70);
        AddReadColumn(grid, "Age", nameof(BibRead.Age), 50);
        AddReadColumn(grid, "Team", nameof(BibRead.Team), 120);
        AddReadColumn(grid, "Report", nameof(BibRead.ReportName), 180);
        AddReadColumn(grid, "Message", nameof(BibRead.Message), 160);
        _readsGrid = grid;
        ReadsHost.Content = grid;
    }

    static void AddReadColumn(DataGrid grid, string header, string path, double width)
    {
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path),
            Width = new DataGridLength(width)
        });
    }

    void SetSendStatus(string status)
    {
        _sendStatus = status;
        _displayCount = 0;
        var first = status.Split(' ', 2)[0];
        if (int.TryParse(first, out var count))
            _displayCount = count;
        if (_settingsUi != null)
            _settingsUi.SendStatusText.Text = status;
        if (JumboDetailText == null)
            return;
        var connected = status.Contains("connected", StringComparison.OrdinalIgnoreCase)
            && !status.StartsWith("Waiting", StringComparison.Ordinal);
        JumboStateText.Text = connected ? "Connected" : "Waiting";
        JumboDot.Fill = (Brush)FindResource(connected ? "GoodBrush" : "WarnBrush");
        JumboDetailText.Text = status;
        PreviewText.Text = connected ? status : "No display client connected.";
        PaintStats();
    }

    void StartClock()
    {
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) =>
        {
            if (!_closing)
                PaintPoll();
        };
        _clock.Start();
        PaintPoll();
    }

    void PaintLive()
    {
        PaintReportHint();
        PaintPoll();
    }

    void PaintReportHint()
    {
        if (StatusText == null)
            return;
        StatusText.Text = _automatic ? "Reports update automatically" : "Automatic refresh is off";
    }

    void PaintPoll()
    {
        if (PollText == null)
            return;
        if (_lastPoll == null)
        {
            PollText.Text = "Waiting for the first poll";
            return;
        }

        var last = _lastPoll.Value.ToString("h:mm:ss tt");
        if (!_automatic)
        {
            PollText.Text = $"Last RDS poll: {last}  •  Automatic refresh is off";
            return;
        }

        var next = _lastPoll.Value.AddSeconds(_refreshSeconds).ToString("h:mm:ss tt");
        PollText.Text = $"Last RDS poll: {last}  •  Next: {next} ({_refreshSeconds}s)";
    }

    Task UpdateHubBannerAsync(Exception exception)
    {
        if (_closing)
            return Task.CompletedTask;
        var show = IsConnectionRefused(exception);
        var reachedServer = exception is RaceDayApiException;
        if (!show && !reachedServer)
            return Task.CompletedTask;
        try
        {
            return Dispatcher.InvokeAsync(() => ShowHubBanner(show)).Task;
        }
        catch (Exception)
        {
            return Task.CompletedTask;
        }
    }

    void ShowHubBanner(bool show)
    {
        if (_closing || HubBanner == null)
            return;
        HubBanner.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    static bool IsConnectionRefused(Exception exception)
    {
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            if (current is SocketException socket && socket.SocketErrorCode == SocketError.ConnectionRefused)
                return true;
            if (current.Message.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    void PaintLink(bool connected, string detail)
    {
        if (ConnectionText == null)
            return;
        ConnectionText.Text = connected ? "Connected to RaceDay Scoring" : "Not connected to RaceDay Scoring";
        ConnectionText.ToolTip = ConnectionText.Text;
        ConnectionDot.Fill = (Brush)FindResource(connected ? "GoodBrush" : "WarnBrush");
        if (detail.StartsWith("Last poll", StringComparison.Ordinal))
            PaintReportHint();
        else if (!string.IsNullOrEmpty(detail))
            StatusText.Text = detail;
        PaintPoll();
    }

    void PaintStats()
    {
        if (StatReports == null)
            return;
        var finishers = 0;
        var waiting = 0;
        foreach (var section in _sections)
        {
            var count = section.Rows?.Count ?? 0;
            finishers += count;
            if (count == 0)
                waiting++;
        }

        StatReports.Text = _sections.Count.ToString();
        StatFinishers.Text = finishers.ToString("N0");
        StatDisplays.Text = _displayCount.ToString();
        StatWaiting.Text = waiting.ToString();
        ReportCountText.Text = _sections.Count == 1 ? "1 Report Detected" : $"{_sections.Count} Reports Detected";
    }

    void ClearActivityButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingLog.Clear();
        ActivityBox?.Clear();
    }

    void PushActivity(string line)
    {
        _pendingLog.Add(line);
        if (_pendingLog.Count > 80)
            _pendingLog.RemoveRange(0, _pendingLog.Count - 60);
        if (ActivityBox == null)
            return;
        ActivityBox.Text = string.Join(Environment.NewLine, _pendingLog);
        ActivityBox.CaretIndex = ActivityBox.Text.Length;
        ActivityBox.ScrollToEnd();
    }

    static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            var nested = FindVisualChild<T>(child);
            if (nested != null)
                return nested;
        }
        return null;
    }

    sealed record DownloadedReport(ReportMetadata Report, ReportPayload Payload);
}

enum AppView
{
    Results,
    Reads,
    Settings
}
