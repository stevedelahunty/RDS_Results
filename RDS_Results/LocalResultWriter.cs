using System.Data;
using System.IO;
using System.Text;
using System.Text.Json;

namespace RDS_Results;

public static class LocalResultWriter
{
    static readonly JsonSerializerOptions ManifestJson = new()
    {
        WriteIndented = true
    };

    public static SaveResult Write(SaveRequest request)
    {
        Directory.CreateDirectory(request.OutputRoot);
        var raceLabel = string.IsNullOrWhiteSpace(request.RaceName) ? "race" : request.RaceName.Trim();
        if (raceLabel.Length > 60)
            raceLabel = raceLabel[..60].Trim();
        var folderName = SanitizeFileName(raceLabel) + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var directory = Path.Combine(request.OutputRoot, folderName);
        Directory.CreateDirectory(directory);

        var manifest = new ResultManifest
        {
            GeneratedAtUtc = DateTime.UtcNow.ToString("o"),
            SourceBaseUrl = request.SourceBaseUrl,
            RaceName = request.RaceName,
            RdgoRaceId = request.RdgoRaceId,
            UploadStatus = "local-only"
        };

        if (!string.IsNullOrWhiteSpace(request.RaceRawJson))
        {
            manifest.RaceFile = "race.json";
            WriteJson(Path.Combine(directory, manifest.RaceFile), request.RaceRawJson);
        }

        if (!string.IsNullOrWhiteSpace(request.ReportsMetaRawJson))
        {
            manifest.ReportsMetaFile = "reports-meta.json";
            WriteJson(Path.Combine(directory, manifest.ReportsMetaFile), request.ReportsMetaRawJson);
        }

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rowCount = 0;
        foreach (var saved in request.Reports)
        {
            var report = saved.Choice.Report;
            var slug = UniqueSlug(usedNames, SanitizeFileName(report.Name), report.ReportBuilderConfigId);
            var jsonFile = slug + ".json";
            var csvFile = slug + ".csv";
            WriteJson(Path.Combine(directory, jsonFile), saved.Payload.RawJson);

            var rows = saved.Payload.Data.Results ?? new();
            WriteCsv(Path.Combine(directory, csvFile), report.Fields ?? new(), rows);
            rowCount += rows.Count;

            manifest.Reports.Add(new ManifestReport
            {
                Name = report.Name,
                RaceName = report.RaceName,
                Kind = saved.Choice.Kind,
                ReportId = report.ReportId,
                ReportBuilderConfigId = report.ReportBuilderConfigId,
                Revision = report.Revision,
                RowCount = rows.Count,
                CsvFile = csvFile,
                JsonFile = jsonFile,
                Fields = (report.Fields ?? new()).Select(field => new ManifestField
                {
                    Name = field.Name,
                    Label = field.Label,
                    ColumnHeader = field.ColumnHeader,
                    ColumnId = field.ColumnId
                }).ToList()
            });
        }

        File.WriteAllText(
            Path.Combine(directory, "manifest.json"),
            JsonSerializer.Serialize(manifest, ManifestJson),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new SaveResult
        {
            DirectoryPath = directory,
            ReportCount = manifest.Reports.Count,
            RowCount = rowCount
        };
    }

    public static DataView BuildTable(ReportMetadata report, ReportData data)
    {
        var table = new DataTable();
        var fields = report.Fields ?? new();
        var headers = Headers(fields, data.Results ?? new());
        var usedColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Count; i++)
        {
            var column = table.Columns.Add(ToColumnName(headers[i], i, usedColumnNames));
            column.Caption = headers[i];
            column.ExtendedProperties["role"] = i < fields.Count ? ResultFields.RoleOf(fields[i]) : ColumnRole.Other;
        }

        foreach (var row in data.Results ?? new())
        {
            var values = new object[headers.Count];
            for (var i = 0; i < headers.Count; i++)
                values[i] = i < row.Count ? FormatCell(row[i]) : "";
            table.Rows.Add(values);
        }

        return table.DefaultView;
    }

    public static IReadOnlyList<string> Headers(IReadOnlyList<FieldDef> fields, IReadOnlyList<List<System.Text.Json.JsonElement>> rows)
    {
        var width = fields.Count;
        foreach (var row in rows)
            width = Math.Max(width, row.Count);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var headers = new List<string>(width);
        for (var i = 0; i < width; i++)
        {
            string name;
            if (i < fields.Count && !string.IsNullOrWhiteSpace(fields[i].Name))
                name = fields[i].Name!.Trim();
            else if (i < fields.Count && !string.IsNullOrWhiteSpace(fields[i].ColumnId))
                name = fields[i].ColumnId!.Trim();
            else
                name = $"Column {i + 1}";

            var candidate = name;
            var suffix = 2;
            while (!used.Add(candidate))
                candidate = $"{name} ({suffix++})";
            headers.Add(candidate);
        }

        return headers;
    }

    static string ToColumnName(string header, int index, HashSet<string> used)
    {
        var cleaned = new string(header.Select(ch => ch is '.' or '(' or ')' or '[' or ']' or '\\' ? ' ' : ch).ToArray()).Trim();
        if (cleaned.Length == 0)
            cleaned = "Column " + (index + 1);

        var candidate = cleaned;
        var suffix = 2;
        while (!used.Add(candidate))
            candidate = cleaned + " " + suffix++;
        return candidate;
    }

    static void WriteCsv(string path, IReadOnlyList<FieldDef> fields, IReadOnlyList<List<System.Text.Json.JsonElement>> rows)
    {
        var headers = Headers(fields, rows);
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", headers.Select(Csv)));
        foreach (var row in rows)
        {
            var cells = new string[headers.Count];
            for (var i = 0; i < headers.Count; i++)
                cells[i] = Csv(i < row.Count ? FormatCell(row[i]) : "");
            builder.AppendLine(string.Join(",", cells));
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    static void WriteJson(string path, string json)
    {
        string text;
        try
        {
            text = RaceDayClient.Pretty(json);
        }
        catch (JsonException)
        {
            text = json;
        }

        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static string FormatCell(System.Text.Json.JsonElement value) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => "",
        System.Text.Json.JsonValueKind.String => value.GetString() ?? "",
        System.Text.Json.JsonValueKind.True => "true",
        System.Text.Json.JsonValueKind.False => "false",
        _ => value.GetRawText()
    };

    static string Csv(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }

    static string UniqueSlug(HashSet<string> usedNames, string sanitizedName, int configId)
    {
        var baseName = string.IsNullOrWhiteSpace(sanitizedName) ? "report" : sanitizedName;
        if (baseName.Length > 60)
            baseName = baseName[..60].Trim();
        var candidate = $"{baseName}-{configId}";
        var suffix = 2;
        while (!usedNames.Add(candidate))
            candidate = $"{baseName}-{configId}-{suffix++}";
        return candidate;
    }

    public static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "report";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Trim().Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
        cleaned = cleaned.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "report" : cleaned;
    }
}
