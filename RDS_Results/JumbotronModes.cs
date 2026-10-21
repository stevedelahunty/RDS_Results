namespace RDS_Results;

public static class JumbotronModes
{
    public const string Welcome = "welcome";
    public const string EventWelcome = "eventWelcome";
    public const string BibReads = "bibReads";
    public const string Selfies = "selfies";
    public const string ScrollingResults = "scrollingResults";
    public const string NovusVox = "novusVox";

    public static readonly JumbotronMode[] All =
    [
        new(Welcome, "Welcome", "App splash screen."),
        new(EventWelcome, "Event welcome", "Custom event graphic with start info and how to view results."),
        new(BibReads, "Bib reads", "Walk-up results as announcer reads arrive."),
        new(Selfies, "Selfies", "Selfie station."),
        new(ScrollingResults, "Scrolling results", "Standings by results set, men then women."),
        new(NovusVox, "NovusVox", "NovusVox program.")
    ];

    public static string Normalize(string? mode)
    {
        var id = (mode ?? "").Trim();
        return All.Any(item => item.Id == id) ? id : BibReads;
    }

    public static string LabelOf(string? mode)
    {
        var id = Normalize(mode);
        return All.First(item => item.Id == id).Label;
    }
}

public readonly record struct JumbotronMode(string Id, string Label, string Detail);
