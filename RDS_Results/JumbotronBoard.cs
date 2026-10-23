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

public sealed class JumbotronBoardColumn
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public string Role { get; init; } = "";
}

public sealed class JumbotronBoardPage
{
    public string Title { get; init; } = "";
    public List<JumbotronBoardColumn> Columns { get; init; } = new();
    public List<JumbotronBoardRow> Rows { get; init; } = new();
}

public sealed class JumbotronBoardRow
{
    public List<string> Cells { get; init; } = new();
}

public static class JumbotronBoard
{
    const int MaxRowsPerPage = 200;

    public static IReadOnlyList<JumbotronBoardPage> Build(
        IEnumerable<ReportSection> sections,
        RaceMetadata? race,
        IReadOnlyList<ScrollReportSetting>? settings)
    {
        var pages = new List<JumbotronBoardPage>();
        foreach (var section in sections)
        {
            var results = section.Payload?.Data.Results;
            if (results == null || results.Count == 0)
                continue;

            var catalog = ScrollFields.From(section.Report);
            var selected = ScrollFields.SelectedKeys(section.Report, settings);
            var columns = ScrollFields.Visible(catalog, selected);
            var fieldCount = section.Report.Fields?.Count ?? 0;
            var drafts = new List<ScrollDraft>();
            foreach (var result in results)
            {
                var raw = new string[fieldCount];
                for (var i = 0; i < fieldCount; i++)
                    raw[i] = i < result.Count ? LocalResultWriter.FormatCell(result[i]).Trim() : "";

                var name = RoleValue(catalog, raw, ColumnRole.Name);
                var bib = RoleValue(catalog, raw, ColumnRole.Bib);
                if (name.Length == 0 && bib.Length == 0)
                    continue;

                drafts.Add(new ScrollDraft
                {
                    Event = EventKey(RoleValue(catalog, raw, ColumnRole.Event), section.Title, race),
                    Gender = RoleValue(catalog, raw, ColumnRole.Gender),
                    Place = RoleValue(catalog, raw, ColumnRole.Place),
                    Name = name,
                    Raw = raw
                });
            }

            if (drafts.Count == 0)
                continue;

            var eventOrder = EventOrder(race);
            var groups = drafts
                .GroupBy(row => row.Event, StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => eventOrder.TryGetValue(group.Key, out var rank) ? rank : 1000 + group.Key.Length)
                .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase);
            var boardColumns = columns.Select(column => new JumbotronBoardColumn
            {
                Key = column.Key,
                Label = column.Label,
                Role = ScrollFields.RoleToken(column.Role)
            }).ToList();

            foreach (var group in groups)
            {
                var byGender = group
                    .GroupBy(row => GenderRank(row.Gender))
                    .OrderBy(item => item.Key)
                    .ToList();
                var onlyBlank = byGender.Count == 1 && byGender[0].Key == 3;
                foreach (var gender in byGender)
                {
                    var ordered = gender
                        .OrderBy(PlaceValue)
                        .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                        .Take(MaxRowsPerPage)
                        .ToList();
                    if (ordered.Count == 0)
                        continue;

                    var rows = new List<JumbotronBoardRow>(ordered.Count);
                    for (var i = 0; i < ordered.Count; i++)
                    {
                        var raw = ordered[i].Raw;
                        var cells = new List<string>(columns.Count);
                        foreach (var column in columns)
                        {
                            if (column.Index >= 0 && column.Index < raw.Length)
                                cells.Add(raw[column.Index]);
                            else if (column.Role == ColumnRole.Place)
                                cells.Add((i + 1).ToString());
                            else
                                cells.Add("");
                        }

                        rows.Add(new JumbotronBoardRow { Cells = cells });
                    }

                    pages.Add(new JumbotronBoardPage
                    {
                        Title = PageTitle(section.Title, group.Key, gender.First().Gender, gender.Key, onlyBlank, race),
                        Columns = boardColumns,
                        Rows = rows
                    });
                }
            }
        }

        return pages;
    }

    static string RoleValue(IReadOnlyList<ScrollField> catalog, string[] raw, ColumnRole role)
    {
        var fallback = "";
        var haveFallback = false;
        foreach (var field in catalog)
        {
            if (field.Role != role || field.Index < 0 || field.Index >= raw.Length)
                continue;
            if (!haveFallback)
            {
                fallback = raw[field.Index];
                haveFallback = true;
            }

            if (raw[field.Index].Length > 0)
                return raw[field.Index];
        }

        return fallback;
    }

    static string PageTitle(string report, string eventKey, string gender, int rank, bool onlyBlank, RaceMetadata? race)
    {
        var title = report.Trim();
        if (title.Length == 0)
            title = "Results";
        if (ShowEvent(eventKey, title, race))
            title = title + " " + eventKey;
        if (!onlyBlank)
        {
            var label = GenderLabel(gender, rank);
            if (!title.EndsWith(label, StringComparison.OrdinalIgnoreCase))
                title = title + " " + label;
        }
        return title;
    }

    static bool ShowEvent(string eventKey, string reportTitle, RaceMetadata? race)
    {
        if (eventKey.Length == 0 || eventKey.Equals("Results", StringComparison.OrdinalIgnoreCase))
            return false;
        var raceName = race?.Race?.Name?.Trim() ?? "";
        if (raceName.Length > 0 && eventKey.Equals(raceName, StringComparison.OrdinalIgnoreCase))
            return false;
        return !reportTitle.Contains(eventKey, StringComparison.OrdinalIgnoreCase);
    }

    static string EventKey(string eventField, string report, RaceMetadata? race)
    {
        var raceName = race?.Race?.Name?.Trim() ?? "";
        var field = eventField.Trim();
        if (field.Length > 0 && !field.Equals(raceName, StringComparison.OrdinalIgnoreCase))
            return field;

        var fromReport = EventFromReport(report, race);
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

    static int PlaceValue(ScrollDraft row) =>
        int.TryParse(row.Place, out var place) ? place : int.MaxValue;

    sealed class ScrollDraft
    {
        public string Event { get; init; } = "";
        public string Gender { get; init; } = "";
        public string Place { get; init; } = "";
        public string Name { get; init; } = "";
        public string[] Raw { get; init; } = Array.Empty<string>();
    }
}
