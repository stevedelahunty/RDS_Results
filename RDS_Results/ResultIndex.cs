namespace RDS_Results;

public sealed class AthleteRow
{
    public string ReportName { get; init; } = "";
    public string Bib { get; init; } = "";
    public string Chip { get; init; } = "";
    public string Name { get; init; } = "";
    public string City { get; init; } = "";
    public string State { get; init; } = "";
    public string Event { get; init; } = "";
    public string Place { get; init; } = "";
    public string ClockTime { get; init; } = "";
    public string ChipTime { get; init; } = "";
    public string Pace { get; init; } = "";
    public string Division { get; init; } = "";
    public string DivisionPlace { get; init; } = "";
    public string Gender { get; init; } = "";
    public string Age { get; init; } = "";
    public string AgeGroup { get; init; } = "";
    public IReadOnlyList<string> Chips { get; init; } = Array.Empty<string>();
}

public sealed class ResultIndex
{
    readonly List<AthleteRow> _rows;

    ResultIndex(List<AthleteRow> rows) => _rows = rows;

    public IReadOnlyList<AthleteRow> Rows => _rows;

    public static ResultIndex Build(IEnumerable<ReportSection> sections)
    {
        var rows = new List<AthleteRow>();
        foreach (var section in sections)
        {
            var payload = section.Payload;
            if (payload == null)
                continue;
            var fields = section.Report.Fields ?? new();
            var roles = fields.Select(ResultFields.RoleOf).ToArray();
            foreach (var result in payload.Data.Results ?? new())
            {
                string Cell(ColumnRole role)
                {
                    for (var i = 0; i < roles.Length && i < result.Count; i++)
                    {
                        if (roles[i] != role)
                            continue;
                        var text = LocalResultWriter.FormatCell(result[i]).Trim();
                        if (text.Length > 0)
                            return text;
                    }
                    return "";
                }

                var chips = new List<string>();
                for (var i = 0; i < roles.Length && i < result.Count; i++)
                {
                    if (roles[i] != ColumnRole.Chip)
                        continue;
                    var text = LocalResultWriter.FormatCell(result[i]).Trim();
                    if (text.Length > 0)
                        chips.Add(text);
                }

                rows.Add(new AthleteRow
                {
                    ReportName = section.Title,
                    Bib = Cell(ColumnRole.Bib),
                    Chip = chips.Count > 0 ? chips[0] : "",
                    Chips = chips,
                    Name = Cell(ColumnRole.Name),
                    City = Cell(ColumnRole.City),
                    State = Cell(ColumnRole.State),
                    Event = Cell(ColumnRole.Event),
                    Place = Cell(ColumnRole.Place),
                    ClockTime = Cell(ColumnRole.Clock),
                    ChipTime = Cell(ColumnRole.ChipTime),
                    Pace = Cell(ColumnRole.Pace),
                    Division = Cell(ColumnRole.Division),
                    DivisionPlace = Cell(ColumnRole.DivisionPlace),
                    Gender = Cell(ColumnRole.Gender),
                    Age = Cell(ColumnRole.Age),
                    AgeGroup = Cell(ColumnRole.AgeGroup)
                });
            }
        }
        return new ResultIndex(rows);
    }

    public AthleteRow? Find(string? bib, string? chip)
    {
        var bibKey = (bib ?? "").Trim();
        var chipKey = (chip ?? "").Trim();
        AthleteRow? best = null;
        var bestRank = (0, 0, 0);
        foreach (var row in _rows)
        {
            var bibHit = SameId(bibKey, row.Bib);
            var chipHit = row.Chips.Any(value => SameId(chipKey, value));
            if (!bibHit && !chipHit)
                continue;
            var score = (bibHit ? 2 : 0) + (chipHit ? 1 : 0);
            var hasTime = row.ChipTime.Length > 0 || row.ClockTime.Length > 0 ? 1 : 0;
            var overall = row.ReportName.Contains("Overall", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            var rank = (score, hasTime, overall);
            if (best != null && rank.CompareTo(bestRank) <= 0)
                continue;
            best = row;
            bestRank = rank;
        }
        return best;
    }

    public static bool SameId(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
            return false;
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;
        return long.TryParse(left, out var a) && long.TryParse(right, out var b) && a == b;
    }

}

public enum ColumnRole
{
    Other,
    Bib,
    Chip,
    Name,
    City,
    State,
    Event,
    Place,
    Clock,
    ChipTime,
    Pace,
    Division,
    DivisionPlace,
    Gender,
    Age,
    AgeGroup
}

static class ResultFields
{
    public static ColumnRole RoleOf(FieldDef field)
    {
        var fromId = Map(Tail(field.ColumnId));
        if (fromId != ColumnRole.Other)
            return fromId;
        return Map((field.Name ?? "").Trim().ToLowerInvariant());
    }

    static string Tail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        var text = value.Trim().ToLowerInvariant();
        var dot = text.LastIndexOf('.');
        return dot >= 0 ? text[(dot + 1)..] : text;
    }

    static ColumnRole Map(string token) => token switch
    {
        "bib" or "bib_num" => ColumnRole.Bib,
        "chip" or "chip_num" or "chip_num2" => ColumnRole.Chip,
        "full_name" or "name" => ColumnRole.Name,
        "city" => ColumnRole.City,
        "state" => ColumnRole.State,
        "place" or "overall_place" => ColumnRole.Place,
        "clock_time" or "gun_time" => ColumnRole.Clock,
        "chip_time" or "net_time" or "finish_time" or "finish" => ColumnRole.ChipTime,
        "pace" => ColumnRole.Pace,
        "age_group_desc" or "division" => ColumnRole.Division,
        "age_place" or "div_place" => ColumnRole.DivisionPlace,
        "gender" => ColumnRole.Gender,
        "age" => ColumnRole.Age,
        "event" or "event_name" => ColumnRole.Event,
        "age_group" => ColumnRole.AgeGroup,
        _ => ColumnRole.Other
    };
}
