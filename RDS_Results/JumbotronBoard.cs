namespace RDS_Results;

public sealed class JumbotronWelcome
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
}

public sealed class JumbotronEventWelcome
{
    public string Title { get; init; } = "";
    public string Start { get; init; } = "";
    public string Events { get; init; } = "";
    public string Hint { get; init; } = "";
    public string ResultsUrl { get; init; } = "";
    public string ImageMime { get; init; } = "";
    public string Image { get; init; } = "";
}

public sealed class JumbotronBoardPage
{
    public string Title { get; init; } = "";
    public List<JumbotronBoardRow> Rows { get; init; } = new();
}

public sealed class JumbotronBoardRow
{
    public string Place { get; init; } = "";
    public string Bib { get; init; } = "";
    public string Name { get; init; } = "";
    public string FinishTime { get; init; } = "";
    public string Pace { get; init; } = "";
    public string City { get; init; } = "";
}

public static class JumbotronBoard
{
    const int MaxRowsPerPage = 200;

    public static IReadOnlyList<JumbotronBoardPage> Build(ResultIndex index, RaceMetadata? race)
    {
        var source = index.Rows;
        var overall = source
            .Where(row => row.ReportName.Contains("Overall", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (overall.Count > 0)
            source = overall;

        var unique = new Dictionary<string, AthleteRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in source)
        {
            if (row.Name.Length == 0 && row.Bib.Length == 0)
                continue;
            unique[$"{EventKey(row, race)}|{row.Bib}|{row.Gender}"] = row;
        }

        var eventOrder = EventOrder(race);
        var groups = unique.Values
            .GroupBy(row => EventKey(row, race), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => eventOrder.TryGetValue(group.Key, out var rank) ? rank : 1000 + group.Key.Length)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase);

        var pages = new List<JumbotronBoardPage>();
        foreach (var group in groups)
        {
            var byGender = group
                .GroupBy(row => GenderRank(row.Gender))
                .OrderBy(item => item.Key);
            foreach (var gender in byGender)
            {
                var ordered = gender
                    .OrderBy(PlaceValue)
                    .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxRowsPerPage)
                    .ToList();
                var rows = ordered
                    .Select((row, index) => new JumbotronBoardRow
                    {
                        Place = (index + 1).ToString(),
                        Bib = row.Bib,
                        Name = row.Name,
                        FinishTime = row.ChipTime.Length > 0 ? row.ChipTime : row.ClockTime,
                        Pace = row.Pace,
                        City = JoinPlace(row.City, row.State)
                    })
                    .ToList();
                if (rows.Count == 0)
                    continue;
                pages.Add(new JumbotronBoardPage
                {
                    Title = $"{group.Key} {GenderLabel(gender.First().Gender, gender.Key)}",
                    Rows = rows
                });
            }
        }

        return pages;
    }

    static string EventKey(AthleteRow row, RaceMetadata? race)
    {
        var raceName = race?.Race?.Name?.Trim() ?? "";
        var field = row.Event.Trim();
        if (field.Length > 0 && !field.Equals(raceName, StringComparison.OrdinalIgnoreCase))
            return field;

        var fromReport = EventFromReport(row.ReportName, race);
        if (fromReport.Length > 0)
            return fromReport;

        if (field.Length > 0)
            return field;
        return raceName.Length > 0 ? raceName : "Results";
    }

    static string EventFromReport(string report, RaceMetadata? race)
    {
        var text = (report ?? "").Trim();
        if (text.Length == 0)
            return "";

        foreach (var item in race?.Events ?? new())
        {
            foreach (var candidate in new[] { item.Name, item.Distance })
            {
                var key = candidate?.Trim() ?? "";
                if (key.Length > 0 && text.Contains(key, StringComparison.OrdinalIgnoreCase))
                    return key;
            }
        }

        var cleaned = text;
        foreach (var word in new[] { "Overall", "Results", "Result", "Male", "Female", "Men", "Women" })
        {
            var start = 0;
            while (true)
            {
                var at = cleaned.IndexOf(word, start, StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                    break;
                var beforeOk = at == 0 || !char.IsLetterOrDigit(cleaned[at - 1]);
                var after = at + word.Length;
                var afterOk = after >= cleaned.Length || !char.IsLetterOrDigit(cleaned[after]);
                if (beforeOk && afterOk)
                {
                    cleaned = cleaned.Remove(at, word.Length);
                    start = at;
                }
                else
                {
                    start = after;
                }
            }
        }

        cleaned = cleaned.Trim(' ', '-', '|', ':', '·');
        if (raceNameLooksLike(cleaned, race))
            return "";
        if (race?.Race?.Name is string raceName &&
            cleaned.StartsWith(raceName, StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned[raceName.Length..].Trim(' ', '-', '|', ':');
        }
        return raceNameLooksLike(cleaned, race) ? "" : cleaned;
    }

    static bool raceNameLooksLike(string value, RaceMetadata? race)
    {
        var raceName = race?.Race?.Name?.Trim() ?? "";
        return value.Length == 0 ||
               (raceName.Length > 0 && value.Equals(raceName, StringComparison.OrdinalIgnoreCase));
    }

    static Dictionary<string, int> EventOrder(RaceMetadata? race)
    {
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var events = race?.Events ?? new();
        for (var i = 0; i < events.Count; i++)
        {
            var item = events[i];
            if (!string.IsNullOrWhiteSpace(item.Name))
                order.TryAdd(item.Name.Trim(), i);
            if (!string.IsNullOrWhiteSpace(item.Distance))
                order.TryAdd(item.Distance.Trim(), i);
        }
        return order;
    }

    static int GenderRank(string gender)
    {
        var text = gender.Trim().ToLowerInvariant();
        return text switch
        {
            "m" or "male" or "man" or "men" => 0,
            "f" or "w" or "female" or "woman" or "women" => 1,
            "" => 3,
            _ => 2
        };
    }

    static string GenderLabel(string gender, int rank) => rank switch
    {
        0 => "Men",
        1 => "Women",
        3 => "Open",
        _ => string.IsNullOrWhiteSpace(gender) ? "Open" : gender.Trim()
    };

    static int PlaceValue(AthleteRow row) =>
        int.TryParse(row.Place, out var place) ? place : int.MaxValue;

    static string JoinPlace(string city, string state)
    {
        if (city.Length == 0)
            return state;
        if (state.Length == 0)
            return city;
        return city + ", " + state;
    }
}
