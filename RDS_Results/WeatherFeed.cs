using System.Net.Http;

namespace RDS_Results;

public sealed class WeatherSnapshot
{
    public string Loc { get; init; } = "";
    public string Temp { get; init; } = "";
    public string Cond { get; init; } = "";
}

public sealed class WeatherFeed : IDisposable
{
    public const string DefaultPlace = "Londonderry, NH";
    static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(15);

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    readonly object _gate = new();
    CancellationTokenSource? _cts;
    WeatherSnapshot? _current;
    string _place = DefaultPlace;

    public WeatherSnapshot? Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public WeatherFeed()
    {
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "RDS_Results/1.0");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/plain");
    }

    public void SetPlace(string? place)
    {
        var next = string.IsNullOrWhiteSpace(place) ? DefaultPlace : place.Trim();
        bool changed;
        CancellationToken token;
        lock (_gate)
        {
            changed = !string.Equals(_place, next, StringComparison.OrdinalIgnoreCase);
            _place = next;
            if (changed)
                _current = null;
            if (_cts == null)
                return;
            token = _cts.Token;
        }

        if (changed)
            _ = Task.Run(() => FetchAndStoreAsync(token));
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_cts != null)
                return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(() => LoopAsync(token));
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
        }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        cts?.Dispose();
        _http.Dispose();
    }

    async Task LoopAsync(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            await FetchAndStoreAsync(cancel).ConfigureAwait(false);
            try
            {
                await Task.Delay(RefreshEvery, cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    async Task FetchAndStoreAsync(CancellationToken cancel)
    {
        try
        {
            WeatherSnapshot? next = await FetchAsync(cancel).ConfigureAwait(false);
            if (next != null)
            {
                lock (_gate)
                    _current = next;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    async Task<WeatherSnapshot?> FetchAsync(CancellationToken cancel)
    {
        string place;
        lock (_gate)
            place = _place;
        var url = "https://wttr.in/" + Uri.EscapeDataString(place) + "?u&format=%l|%t|%C";
        using HttpResponseMessage response = await _http.GetAsync(url, cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;
        string body = (await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false)).Trim();
        if (string.IsNullOrWhiteSpace(body) || body.Contains('<', StringComparison.Ordinal))
            return null;
        string[] parts = body.Split('|');
        string temp = parts.Length > 1 ? parts[1].Trim() : "";
        string cond = parts.Length > 2 ? parts[2].Trim() : "";
        if (temp.StartsWith('+'))
            temp = temp[1..];
        if (temp.Length == 0 && cond.Length == 0)
            return null;
        return new WeatherSnapshot { Loc = place, Temp = temp, Cond = cond };
    }
}
