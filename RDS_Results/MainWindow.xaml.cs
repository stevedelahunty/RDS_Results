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
    readonly WeatherFeed _weather = new();
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
    bool _suppressJumboMode;
    string _liveJumbotronMode = JumbotronModes.BibReads;
    int _nextReadId = 1;
    List<string>? _savedLocationNames;
    readonly AppSettings _settings = new();
    readonly List<string> _pendingLog = new();
    SettingsPanel? _settingsUi;
    Popup? _scrollPopup;
    DataGrid? _readsGrid;
    bool _gridChrome;
    string _sendStatus = "Bib reads stay on this computer until sending starts.";
    string _cameraSummary = "Not set";
    bool _cameraLive;
    bool _rdsConnected;
    int _displayCount;
    DispatcherTimer? _clock;
    CameraPreview? _camera;
    string _openCameraUrl = "";

    public MainWindow()
    {
        LoadSettings();
        AppThemes.Apply(_theme);
        InitializeComponent();
        _bibSender.Weather = () => _weather.Current;
        _bibSender.RaceStart = CurrentRaceStart;
        _weather.SetPlace(_settings.WeatherLocation);
        _weather.Start();
        ReportSections.ItemsSource = _sections;
        LocationChoices.ItemsSource = _locations;
        FillJumboModeBox();
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
        ApplyCamera();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _clock?.Stop();
        SaveSettings();
        StopCamera();
        _pollCts.Cancel();
        StopListening();
        _bibSender.Dispose();
        _weather.Dispose();
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
        PaintStatusBar();
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
        var listsChanged = removed.Count > 0;
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
                listsChanged = true;
                continue;
            }

            var scroller = CaptureGridScroll(section);
            if (!section.Apply(item.Report, item.Payload))
                continue;
            listsChanged = true;
            RestoreGridScroll(scroller);
        }

        EmptyStateText.Visibility = _sections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (listsChanged || !_haveSnapshot)
            LayoutBoard();
        RematchReads();
        if (listsChanged || !_haveSnapshot)
            PublishJumbotron();
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
            PaintRaceClock();
            _bibSender.SendPingNow();
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
        PaintRaceClock();
        _bibSender.SendPingNow();
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
            _settings.JumbotronHost = _settingsUi.JumboHostBox.Text.Trim();
            if (!int.TryParse(_settingsUi.SendPortBox.Text.Trim(), out var typed) || typed is < 1 or > 65535)
            {
                _bibSender.Stop();
                SetSendStatus("Jumbotron port must be a number from 1 to 65535.");
                return;
            }
            _settings.SendPort = typed;
        }

        if (string.IsNullOrWhiteSpace(_settings.JumbotronHost))
        {
            _bibSender.Stop();
            SetSendStatus("Enter the jumbotron host.");
            return;
        }

        if (_settings.SendPort is < 1 or > 65535)
        {
            _bibSender.Stop();
            SetSendStatus("Jumbotron port must be a number from 1 to 65535.");
            return;
        }

        PublishJumbotron();
        if (!_bibSender.Start(_settings.JumbotronHost, _settings.SendPort, _liveJumbotronMode, ShowSendStatus))
            return;

        foreach (var read in _reads.Reverse())
            _bibSender.Publish(read, false);
        PublishJumbotron();
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
        PaintModeButtons();
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
            if (!string.IsNullOrWhiteSpace(settings.JumbotronHost))
                _settings.JumbotronHost = settings.JumbotronHost.Trim();
            if (settings.SendPort is > 0 and <= 65535)
                _settings.SendPort = settings.SendPort;
            _settings.JumbotronMode = JumbotronModes.Normalize(settings.JumbotronMode);
            _liveJumbotronMode = _settings.JumbotronMode;
            _settings.RaceStartTime = settings.RaceStartTime ?? "";
            _settings.WeatherLocation = string.IsNullOrWhiteSpace(settings.WeatherLocation)
                ? WeatherFeed.DefaultPlace
                : settings.WeatherLocation.Trim();
            _settings.EventWelcomeImage = settings.EventWelcomeImage ?? "";
            _settings.EventWelcomeHint = settings.EventWelcomeHint ?? "";
            _settings.EventWelcomeUrl = settings.EventWelcomeUrl ?? "";
            _settings.CameraIp = settings.CameraIp ?? "";
            _settings.CameraTemplate = CameraUrl.NormalizeTemplate(settings.CameraTemplate);
            _settings.CameraAdvanced = settings.CameraAdvanced;
            _settings.CameraUseExact = settings.CameraUseExact;
            _settings.CameraExactUrl = settings.CameraExactUrl ?? "";
            _settings.ScrollReports = settings.ScrollReports ?? new();
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
            _settings.JumbotronMode = JumbotronModes.Normalize(_settings.JumbotronMode);
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
        _settings.JumbotronHost = ui.JumboHostBox.Text.Trim();
        if (int.TryParse(ui.SendPortBox.Text.Trim(), out var sendPort) && sendPort is > 0 and <= 65535)
            _settings.SendPort = sendPort;
        _settings.RaceStartTime = ui.RaceStartBox.Text.Trim();
        _settings.WeatherLocation = string.IsNullOrWhiteSpace(ui.WeatherBox.Text)
            ? WeatherFeed.DefaultPlace
            : ui.WeatherBox.Text.Trim();
        _settings.EventWelcomeHint = ui.EventHintBox.Text.Trim();
        _settings.EventWelcomeUrl = ui.EventUrlBox.Text.Trim();
        _settings.CameraIp = ui.CameraIpBox.Text.Trim();
        _settings.CameraTemplate = CameraUrl.NormalizeTemplate(ui.CameraTemplateBox.Text);
        _settings.CameraAdvanced = ui.CameraAdvancedBox.IsChecked == true;
        _settings.CameraUseExact = ui.CameraExactBox.IsChecked == true;
        _settings.CameraExactUrl = ui.CameraUrlBox.Text.Trim();
    }

    void EnsureSettings()
    {
        if (_settingsUi != null)
            return;
        _suppressSettings = true;
        var panel = new SettingsPanel { Host = this };
        _settingsUi = panel;
        ApplySettingsToUi(panel);
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
        ui.JumboHostBox.Text = string.IsNullOrWhiteSpace(_settings.JumbotronHost) ? "127.0.0.1" : _settings.JumbotronHost;
        ui.SendPortBox.Text = _settings.SendPort.ToString();
        ui.SendStatusText.Text = _sendStatus;
        ui.RaceStartBox.Text = _settings.RaceStartTime;
        ui.WeatherBox.Text = string.IsNullOrWhiteSpace(_settings.WeatherLocation)
            ? WeatherFeed.DefaultPlace
            : _settings.WeatherLocation;
        ui.EventGraphicBox.Text = _settings.EventWelcomeImage;
        ui.EventHintBox.Text = _settings.EventWelcomeHint;
        ui.EventUrlBox.Text = _settings.EventWelcomeUrl;
        ui.CameraIpBox.Text = _settings.CameraIp;
        ui.CameraTemplateBox.Text = CameraUrl.NormalizeTemplate(_settings.CameraTemplate);
        ui.CameraAdvancedBox.IsChecked = _settings.CameraAdvanced;
        ui.CameraExactBox.IsChecked = _settings.CameraUseExact;
        ui.CameraUrlBox.Text = _settings.CameraExactUrl;
        EnsureModeButtons(ui);
        PaintModeButtons();
        PaintCameraSettings();
    }

    void EnsureModeButtons(SettingsPanel ui)
    {
        if (ui.ModeButtons.Children.Count > 0)
            return;
        foreach (var mode in JumbotronModes.All)
        {
            var button = new Button
            {
                Content = mode.Label,
                Tag = mode.Id,
                Style = (Style)FindResource("PillButton"),
                ToolTip = mode.Detail,
                Margin = new Thickness(0, 0, 8, 8)
            };
            button.Click += (_, _) => SetJumbotronMode((string)button.Tag, persist: true);
            ui.ModeButtons.Children.Add(button);
        }
    }

    void SetJumbotronMode(string mode, bool persist)
    {
        var id = JumbotronModes.Normalize(mode);
        var liveChanged = _liveJumbotronMode != id;
        var defaultChanged = persist && _settings.JumbotronMode != id;
        _liveJumbotronMode = id;
        if (persist)
            _settings.JumbotronMode = id;
        PaintModeButtons();
        PaintLiveModeBox();
        SetSendStatus(_sendStatus);
        if (defaultChanged)
            SaveSettings();
        if (liveChanged)
        {
            PublishJumbotron();
            _bibSender.SetMode(id);
            if (id == JumbotronModes.EventWelcome)
                LogEventWelcomeGraphic();
            Log(persist
                ? "Jumbotron default mode is now " + JumbotronModes.LabelOf(id) + "."
                : "Jumbotron mode is now " + JumbotronModes.LabelOf(id) + " for this session.");
            return;
        }

        if (defaultChanged)
            Log("Jumbotron default mode is now " + JumbotronModes.LabelOf(id) + ".");
    }

    void FillJumboModeBox()
    {
        if (JumboModeBox == null)
            return;
        JumboModeBox.ItemsSource = JumbotronModes.All;
        PaintLiveModeBox();
    }

    void PaintLiveModeBox()
    {
        if (JumboModeBox == null)
            return;
        _suppressJumboMode = true;
        JumboModeBox.SelectedValue = JumbotronModes.Normalize(_liveJumbotronMode);
        _suppressJumboMode = false;
    }

    void JumboModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressJumboMode || JumboModeBox.SelectedValue is not string id)
            return;
        SetJumbotronMode(id, persist: false);
    }

    internal void BrowseEventGraphicButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Event welcome graphic",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp|All files|*.*",
            FileName = _settings.EventWelcomeImage
        };
        if (dialog.ShowDialog(this) != true)
            return;
        _settings.EventWelcomeImage = dialog.FileName;
        if (_settingsUi != null)
            _settingsUi.EventGraphicBox.Text = dialog.FileName;
        SaveSettings();
        PublishJumbotron();
        Log("Event welcome graphic is " + Path.GetFileName(dialog.FileName) + ".");
    }

    internal void ClearEventGraphicButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.EventWelcomeImage = "";
        if (_settingsUi != null)
            _settingsUi.EventGraphicBox.Text = "";
        SaveSettings();
        PublishJumbotron();
        Log("Event welcome graphic cleared.");
    }

    internal void EventWelcome_LostFocus(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        PublishJumbotron();
    }

    internal void RaceStart_LostFocus(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        if (!string.IsNullOrWhiteSpace(_settings.RaceStartTime)
            && !RaceStartTime.TryParse(_settings.RaceStartTime, out _))
        {
            Log("Race start time was not understood. Use a time such as 7:30 AM.");
            return;
        }

        _bibSender.SendPingNow();
        PaintRaceClock();
        var snap = CurrentRaceStart();
        Log(snap == null ? "Race start time cleared." : "Race start time is " + snap.Display + ".");
    }

    internal void Weather_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressSettings)
            return;
        SaveSettings();
        _weather.SetPlace(_settings.WeatherLocation);
        _bibSender.SendPingNow();
        Log("Weather location is " + _settings.WeatherLocation + ".");
    }

    RaceStartSnapshot? CurrentRaceStart() =>
        RaceStartTime.Resolve(_settings.RaceStartTime, _race?.Race?.StartDate, DateTime.Now);

    internal void Camera_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_suppressSettings)
            return;
        SaveSettings();
        PaintCameraSettings();
        ApplyCamera();
    }

    internal void CameraAdvanced_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSettings || _settingsUi == null)
            return;
        _settings.CameraAdvanced = _settingsUi.CameraAdvancedBox.IsChecked == true;
        PaintCameraSettings();
        SaveSettings();
    }

    internal void CameraExact_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressSettings || _settingsUi == null)
            return;
        _settings.CameraUseExact = _settingsUi.CameraExactBox.IsChecked == true;
        SaveSettings();
        PaintCameraSettings();
        ApplyCamera();
    }

    void PaintCameraSettings()
    {
        if (_settingsUi == null)
            return;
        _settingsUi.CameraAdvancedPanel.Visibility = _settings.CameraAdvanced ? Visibility.Visible : Visibility.Collapsed;
        var exact = _settings.CameraAdvanced && _settings.CameraUseExact;
        _settingsUi.CameraIpBox.IsEnabled = !exact;
        _settingsUi.CameraTemplateBox.IsEnabled = !exact;
        _settingsUi.CameraUrlBox.IsEnabled = exact;
        var url = CameraUrl.Resolve(_settings);
        _settingsUi.CameraBuiltUrlText.Text = url.Length == 0
            ? (_settings.CameraUseExact
                ? "Enter an exact RTSP URL."
                : "Enter a camera IP to build the RTSP URL.")
            : CameraUrl.Redact(url);
    }

    void ApplyCamera()
    {
        if (_closing || CameraHintText == null)
            return;
        var url = CameraUrl.Resolve(_settings);
        if (url.Length == 0)
        {
            StopCameraPlayback();
            SetCameraStatus("Not set", "No camera configured.", "Set a camera IP in Settings.");
            return;
        }

        if (url == _openCameraUrl)
            return;

        if (!EnsureCameraPlayer())
            return;

        try
        {
            _camera!.Play(url);
            _openCameraUrl = url;
            SetCameraStatus("Opening", "Opening the camera.", null);
            Log("Camera opening " + CameraUrl.Redact(url) + ".");
        }
        catch (Exception ex)
        {
            SetCameraStatus("Failed", "Camera failed.", "Camera failed. " + ex.Message);
            Log("Camera failed. " + ex.Message);
        }
    }

    void SetCameraStatus(string summary, string caption, string? overlay)
    {
        _cameraSummary = summary;
        _cameraLive = summary == "Live";
        if (PreviewText != null)
        {
            PreviewText.Text = caption;
            PreviewText.ToolTip = string.IsNullOrEmpty(overlay) ? caption : overlay;
        }

        if (CameraHintText != null)
        {
            if (string.IsNullOrEmpty(overlay))
            {
                CameraHintText.Visibility = Visibility.Collapsed;
            }
            else
            {
                CameraHintText.Text = overlay;
                CameraHintText.Visibility = Visibility.Visible;
            }
        }

        PaintStatusBar();
    }

    bool EnsureCameraPlayer()
    {
        if (_camera != null)
            return true;
        if (CameraImage == null)
            return false;
        _camera = new CameraPreview(CameraImage, line =>
        {
            if (_closing)
                return;
            if (line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                SetCameraStatus("Failed", "Camera failed.", line);
            else if (line.StartsWith("Camera stream ", StringComparison.Ordinal))
            {
                var where = string.IsNullOrWhiteSpace(_settings.CameraIp) ? "" : " · " + _settings.CameraIp.Trim();
                SetCameraStatus("Live", "Live" + where, null);
            }
            Log(line);
        });
        if (_camera.Start())
            return true;
        _camera.Dispose();
        _camera = null;
        SetCameraStatus("Failed", "Camera failed.", "Camera player failed to start.");
        return false;
    }

    void StopCameraPlayback()
    {
        _openCameraUrl = "";
        _camera?.StopPlayback();
    }

    void StopCamera()
    {
        StopCameraPlayback();
        _camera?.Dispose();
        _camera = null;
    }

    void PublishJumbotron()
    {
        if (!(_settings.SendReads ?? true))
            return;
        _bibSender.SetWelcome(BuildWelcome());
        _bibSender.SetEventWelcome(BuildEventWelcome());
        _bibSender.SetBoard(JumbotronBoard.Build(_sections, _race, _settings.ScrollReports));
    }

    internal void ChooseScrollFields(ReportSection section, UIElement anchor)
    {
        if (_scrollPopup != null)
            _scrollPopup.IsOpen = false;

        var catalog = ScrollFields.From(section.Report);
        var selected = ScrollFields.SelectedKeys(section.Report, _settings.ScrollReports);
        var ink = (Brush)FindResource("InkBrush");
        var muted = (Brush)FindResource("MutedBrush");
        var panel = new StackPanel { Margin = new Thickness(12), MaxWidth = 320 };
        panel.Children.Add(new TextBlock
        {
            Text = section.Title,
            FontWeight = FontWeights.SemiBold,
            Foreground = ink,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Place and name always show on scrolling results. Check the other fields from this report.",
            Margin = new Thickness(0, 4, 0, 8),
            Foreground = muted,
            TextWrapping = TextWrapping.Wrap
        });

        var place = catalog.FirstOrDefault(field => field.Required && field.Role == ColumnRole.Place);
        var name = catalog.FirstOrDefault(field => field.Required && field.Role == ColumnRole.Name);
        AddLockedField(panel, place?.Label ?? "Place", ink);
        if (name == null)
            AddLockedField(panel, "Name", ink);
        foreach (var field in catalog.OrderBy(item => item.Index))
        {
            if (place != null && ReferenceEquals(field, place))
                continue;
            if (field.Required)
            {
                AddLockedField(panel, field.Label, ink);
                continue;
            }

            var box = new CheckBox
            {
                Content = field.Label,
                IsChecked = selected.Contains(field.Key),
                Foreground = ink,
                Margin = new Thickness(0, 3, 0, 3),
                Tag = field.Key
            };
            box.Checked += (_, _) => SaveScrollFields(section, catalog, panel);
            box.Unchecked += (_, _) => SaveScrollFields(section, catalog, panel);
            panel.Children.Add(box);
        }

        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 4,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = (Brush)FindResource("PanelBrush"),
                BorderBrush = (Brush)FindResource("LineBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = new ScrollViewer
                {
                    MaxHeight = 420,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = panel
                }
            }
        };
        _scrollPopup = popup;
        popup.IsOpen = true;
    }

    static void AddLockedField(Panel panel, string label, Brush ink)
    {
        panel.Children.Add(new CheckBox
        {
            Content = label,
            IsChecked = true,
            IsEnabled = false,
            Foreground = ink,
            Margin = new Thickness(0, 3, 0, 3)
        });
    }

    void SaveScrollFields(ReportSection section, IReadOnlyList<ScrollField> catalog, Panel panel)
    {
        var picked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in panel.Children)
        {
            if (child is not CheckBox box || box.Tag is not string key || box.IsChecked != true || !box.IsEnabled)
                continue;
            picked.Add(key);
        }

        var ordered = catalog
            .Where(field => picked.Contains(field.Key))
            .OrderBy(field => field.Index)
            .Select(field => field.Key)
            .ToList();
        var reportKey = ScrollFields.ReportKey(section.Report);
        _settings.ScrollReports ??= new();
        _settings.ScrollReports.RemoveAll(item => string.Equals(item.Key, reportKey, StringComparison.OrdinalIgnoreCase));
        _settings.ScrollReports.Add(new ScrollReportSetting { Key = reportKey, Fields = ordered });
        SaveSettings();
        PublishJumbotron();

        var labels = catalog
            .Where(field => picked.Contains(field.Key))
            .OrderBy(field => field.Index)
            .Select(field => field.Label);
        Log("Scrolling fields for " + section.Title + ": " + string.Join(", ", new[] { "Place", "Name" }.Concat(labels)) + ".");
    }

    JumbotronWelcome BuildWelcome()
    {
        var info = _race?.Race;
        var title = string.IsNullOrWhiteSpace(info?.Name) ? "Welcome" : info!.Name!.Trim();
        var parts = new List<string>();
        var start = FormatRaceDay(info?.StartDate);
        if (start != "—")
            parts.Add(start);
        var course = DescribeCourse(_race?.Events);
        if (course.Length > 0 && course != "Distance not set")
            parts.Add(course);
        return new JumbotronWelcome
        {
            Title = title,
            Detail = parts.Count == 0 ? "Race day" : string.Join("  ·  ", parts)
        };
    }

    JumbotronEventWelcome BuildEventWelcome()
    {
        var info = _race?.Race;
        var image = JumbotronImage.Encode(_settings.EventWelcomeImage);
        return new JumbotronEventWelcome
        {
            Title = string.IsNullOrWhiteSpace(info?.Name) ? "Event welcome" : info!.Name!.Trim(),
            Start = FormatRaceStart(info?.StartDate),
            Events = DescribeEvents(_race?.Events),
            Hint = _settings.EventWelcomeHint.Trim(),
            ResultsUrl = _settings.EventWelcomeUrl.Trim(),
            ImageMime = image.Mime,
            Image = image.Data
        };
    }

    void LogEventWelcomeGraphic()
    {
        var path = _settings.EventWelcomeImage;
        if (string.IsNullOrWhiteSpace(path))
        {
            Log("Event welcome has no graphic. Choose one in Settings.");
            return;
        }

        var image = JumbotronImage.Encode(path);
        if (image.Data.Length == 0)
        {
            Log("Event welcome graphic was not sent. Could not read " + path + ".");
            return;
        }

        Log("Event welcome graphic sent (" + Path.GetFileName(path) + ").");
    }

    static string FormatRaceStart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        if (!DateTime.TryParse(value, out var when))
            return value.Trim();
        return when.TimeOfDay == TimeSpan.Zero
            ? when.ToString("MMMM d, yyyy")
            : when.ToString("MMMM d, yyyy  h:mm tt");
    }

    static string DescribeEvents(List<RaceEvent>? events)
    {
        var names = (events ?? new())
            .Where(item => !item.Hidden)
            .Select(item =>
            {
                if (!string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Distance)
                    && !item.Name.Contains(item.Distance, StringComparison.OrdinalIgnoreCase))
                    return item.Name.Trim() + " " + item.Distance.Trim();
                if (!string.IsNullOrWhiteSpace(item.Name))
                    return item.Name.Trim();
                return item.Distance?.Trim() ?? "";
            })
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return string.Join("   ·   ", names);
    }

    void PaintModeButtons()
    {
        if (_settingsUi == null)
            return;
        var selected = JumbotronModes.Normalize(_settings.JumbotronMode);
        foreach (var child in _settingsUi.ModeButtons.Children)
        {
            if (child is not Button button || button.Tag is not string id)
                continue;
            PaintThemeButton(button, id == selected);
        }
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
        var connected = status.StartsWith("Connected", StringComparison.OrdinalIgnoreCase);
        _displayCount = connected ? 1 : 0;
        if (_settingsUi != null)
            _settingsUi.SendStatusText.Text = status;
        if (JumboDetailText == null)
            return;
        var mode = JumbotronModes.LabelOf(_liveJumbotronMode);
        JumboStateText.Text = connected ? "Connected · " + mode : "Waiting · " + mode;
        JumboDot.Fill = (Brush)FindResource(connected ? "GoodBrush" : "WarnBrush");
        JumboDetailText.Text = CompactSendStatus(status);
        JumboDetailText.ToolTip = status;
        PaintStats();
        PaintStatusBar();
    }

    static string CompactSendStatus(string status)
    {
        if (status.StartsWith("Jumbotron connection failed", StringComparison.OrdinalIgnoreCase))
        {
            var retry = status.LastIndexOf("Retrying ", StringComparison.Ordinal);
            return retry >= 0
                ? "Cannot reach the jumbotron. " + status[retry..]
                : "Cannot reach the jumbotron.";
        }

        return status;
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
        PaintRaceClock();
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

    void PaintRaceClock()
    {
        if (RaceClockText == null)
            return;
        var now = DateTime.Now;
        RaceClockText.Text = now.ToString("h:mm:ss tt");
        if (!TryRaceStart(out var start))
        {
            RaceElapsedText.Text = "Start time not set";
            return;
        }

        if (now < start)
        {
            RaceElapsedText.Text = "Starts " + start.ToString("h:mm tt");
            return;
        }

        RaceElapsedText.Text = "Elapsed " + FormatElapsed(now - start);
    }

    bool TryRaceStart(out DateTime start)
    {
        var snap = CurrentRaceStart();
        if (snap == null)
        {
            start = default;
            return false;
        }

        start = snap.At;
        return true;
    }

    static string FormatElapsed(TimeSpan span)
    {
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}";
        return $"{span.Minutes}:{span.Seconds:00}";
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
        _rdsConnected = connected;
        PaintStatusBar();
        if (detail.StartsWith("Last poll", StringComparison.Ordinal))
            PaintReportHint();
        else if (!string.IsNullOrEmpty(detail))
            StatusText.Text = detail;
        PaintPoll();
    }

    void PaintStatusBar()
    {
        if (StatusJumboText == null)
            return;

        var jumboOn = _sendStatus.StartsWith("Connected", StringComparison.OrdinalIgnoreCase);
        var mode = JumbotronModes.LabelOf(_liveJumbotronMode);
        StatusJumboDot.Fill = (Brush)FindResource(jumboOn ? "GoodBrush" : "WarnBrush");
        StatusJumboText.Text = jumboOn ? "Connected" : "Not connected";
        StatusJumboText.ToolTip = mode + ". " + _sendStatus;

        var rdsText = _rdsConnected
            ? (_automatic ? "Receiving" : "Connected")
            : (ConnectionText.Text.StartsWith("Connecting", StringComparison.Ordinal) ? "Connecting" : "Not connected");
        StatusRdsDot.Fill = (Brush)FindResource(_rdsConnected ? "GoodBrush" : "WarnBrush");
        StatusRdsText.Text = rdsText;
        StatusRdsText.ToolTip = _lastPoll == null
            ? ConnectionText.Text
            : ConnectionText.Text + ". Last poll " + _lastPoll.Value.ToString("h:mm:ss tt") + ".";

        var cameraOn = _cameraSummary == "Live";
        StatusCameraDot.Fill = (Brush)FindResource(cameraOn ? "GoodBrush" : "WarnBrush");
        StatusCameraText.Text = _cameraSummary;
        StatusCameraText.ToolTip = PreviewText?.ToolTip ?? PreviewText?.Text;
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
