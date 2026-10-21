namespace RDS_Display;

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
        new(EventWelcome, "Event welcome", "Waiting for the event graphic."),
        new(BibReads, "Bib reads", "Walk-up results as announcer reads arrive."),
        new(Selfies, "Selfies", "Waiting for the selfie station."),
        new(ScrollingResults, "Scrolling results", "Waiting for standings pages."),
        new(NovusVox, "NovusVox", "Waiting for the NovusVox program.")
    ];

    public static JumbotronMode Describe(string? mode)
    {
        var id = (mode ?? "").Trim();
        if (id.Length == 0)
            id = BibReads;
        return All.FirstOrDefault(item => item.Id == id) is { Id.Length: > 0 } known
            ? known
            : new JumbotronMode(id, id, "The jumbotron is following this mode.");
    }
}

public readonly record struct JumbotronMode(string Id, string Label, string Detail);
