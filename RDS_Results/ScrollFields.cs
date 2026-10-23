namespace RDS_Results;

public sealed class ScrollReportSetting
{
    public string Key { get; set; } = "";
    public List<string> Fields { get; set; } = new();
}

public sealed class ScrollField
{
    public ScrollField(string key, string label, ColumnRole role, int index, bool required)
    {
        Key = key;
        Label = label;
        Role = role;
        Index = index;
        Required = required;
    }

    public string Key { get; }
    public string Label { get; }
    public ColumnRole Role { get; }
    public int Index { get; }
    public bool Required { get; }
}

public static class ScrollFields
{
    public static string ReportKey(ReportMetadata report)
    {
        if (report.ReportBuilderConfigId != 0)
            return report.ReportBuilderConfigId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var name = (report.Name ?? "").Trim();
        return name.Length == 0 ? "report" : "name:" + name;
    }

    public static string RoleToken(ColumnRole role) => role switch
    {
        ColumnRole.Place => "place",
        ColumnRole.Name => "name",
        ColumnRole.Bib => "bib",
        ColumnRole.Chip => "chip",
        ColumnRole.City => "city",
        ColumnRole.State => "state",
        ColumnRole.Event => "event",
        ColumnRole.Clock => "clock",
        ColumnRole.ChipTime => "net",
        ColumnRole.Pace => "pace",
        ColumnRole.Division => "division",
        ColumnRole.DivisionPlace => "agePlace",
        ColumnRole.Gender => "gender",
        ColumnRole.Age => "age",
        ColumnRole.AgeGroup => "ageGroup",
        _ => "other"
    };

    public static IReadOnlyList<ScrollField> From(ReportMetadata report)
    {
        var fields = report.Fields ?? new();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ScrollField>();
        var placeSeen = false;
        var nameSeen = false;
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            if (!HasIdentity(field))
                continue;
            var role = ResultFields.RoleOf(field);
            var required = false;
            if (role == ColumnRole.Place && !placeSeen)
            {
                required = true;
                placeSeen = true;
            }
            else if (role == ColumnRole.Name && !nameSeen)
            {
                required = true;
                nameSeen = true;
            }

            list.Add(new ScrollField(KeyOf(field, i, used), LabelOf(field, role), role, i, required));
        }

        return list;
    }

    public static HashSet<string> SelectedKeys(ReportMetadata report, IReadOnlyList<ScrollReportSetting>? settings)
    {
        var catalog = From(report);
        var key = ReportKey(report);
        ScrollReportSetting? saved = null;
        if (settings != null)
        {
            foreach (var item in settings)
            {
                if (!string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
                    continue;
                saved = item;
                break;
            }
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (saved == null)
        {
            foreach (var field in catalog)
            {
                if (IsDefault(field))
                    result.Add(field.Key);
            }
            return result;
        }

        var optional = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in catalog)
        {
            if (!field.Required)
                optional.Add(field.Key);
        }

        foreach (var field in saved.Fields ?? new())
        {
            if (optional.Contains(field))
                result.Add(field);
        }

        return result;
    }

    public static IReadOnlyList<ScrollField> Visible(IReadOnlyList<ScrollField> catalog, IReadOnlySet<string> selected)
    {
        var chosen = new List<ScrollField>();
        foreach (var field in catalog)
        {
            if (field.Required || selected.Contains(field.Key))
                chosen.Add(field);
        }

        ScrollField? place = null;
        ScrollField? name = null;
        foreach (var field in chosen)
        {
            if (place == null && field.Role == ColumnRole.Place && field.Required)
                place = field;
            if (name == null && field.Role == ColumnRole.Name && field.Required)
                name = field;
        }

        place ??= new ScrollField("place", "Place", ColumnRole.Place, -1, true);
        var syntheticName = name == null;
        name ??= new ScrollField("name", "Name", ColumnRole.Name, int.MinValue, true);

        var rest = new List<ScrollField>();
        foreach (var field in chosen)
        {
            if (ReferenceEquals(field, place))
                continue;
            rest.Add(field);
        }

        if (syntheticName)
            rest.Add(name);
        rest.Sort((a, b) => a.Index.CompareTo(b.Index));

        var ordered = new List<ScrollField> { place };
        ordered.AddRange(rest);
        return ordered;
    }

    static bool IsDefault(ScrollField field) =>
        !field.Required && field.Role is ColumnRole.Bib or ColumnRole.ChipTime or ColumnRole.Pace;

    static bool HasIdentity(FieldDef field) =>
        !string.IsNullOrWhiteSpace(field.Name) ||
        !string.IsNullOrWhiteSpace(field.Label) ||
        !string.IsNullOrWhiteSpace(field.ColumnHeader) ||
        !string.IsNullOrWhiteSpace(field.ColumnId);

    static string KeyOf(FieldDef field, int index, HashSet<string> used)
    {
        var raw = !string.IsNullOrWhiteSpace(field.ColumnId)
            ? field.ColumnId!.Trim()
            : !string.IsNullOrWhiteSpace(field.Name)
                ? field.Name!.Trim()
                : "column-" + (index + 1);
        var key = raw;
        var suffix = 2;
        while (!used.Add(key))
            key = raw + " #" + suffix++;
        return key;
    }

    static string LabelOf(FieldDef field, ColumnRole role)
    {
        if (!string.IsNullOrWhiteSpace(field.Label))
            return field.Label.Trim();
        if (!string.IsNullOrWhiteSpace(field.Name))
            return field.Name.Trim();
        if (role != ColumnRole.Other)
            return RoleLabel(role);
        if (!string.IsNullOrWhiteSpace(field.ColumnHeader))
            return field.ColumnHeader.Trim();
        return "Field";
    }

    static string RoleLabel(ColumnRole role) => role switch
    {
        ColumnRole.Place => "Place",
        ColumnRole.Name => "Name",
        ColumnRole.Bib => "Bib",
        ColumnRole.ChipTime => "Chip time",
        ColumnRole.Clock => "Clock time",
        ColumnRole.Pace => "Pace",
        ColumnRole.City => "City",
        ColumnRole.State => "State",
        ColumnRole.Gender => "Gender",
        ColumnRole.Age => "Age",
        ColumnRole.AgeGroup => "Age group",
        ColumnRole.Division => "Division",
        ColumnRole.DivisionPlace => "Age place",
        ColumnRole.Chip => "Chip",
        ColumnRole.Event => "Event",
        _ => "Field"
    };
}
