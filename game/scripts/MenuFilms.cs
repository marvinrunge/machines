using Godot;

namespace Machines.Game;

/// <summary>
/// The original's films between its screens (MachGuiStartupScreens's CTX_TRANSITION, <c>flics/&lt;before&gt;&lt;after&gt;.smk</c>).
/// A film exists only between screens that lie next to each other on the original's board, so where the remake's pages
/// lead further in one step, the films of the way the original went play in turn. With the options' Menu Transitions off,
/// without the game copy's films, or where no way leads, the page simply follows.
/// </summary>
public static class MenuFilms
{
    /// <summary>The original's names of its screens, which its films are named after.</summary>
    public const string Main = "sa", Multiplayer = "sb", Join = "sc", Room = "sd", InGame = "se", Options = "sf", Load = "sg", Save = "sh",
        SinglePlayer = "si", Debriefing = "sj", Briefing = "sk", Scenario = "sl", CampaignPlayers = "sm", Skirmish = "sn", HotKeys = "so",
        Settings = "sp", Statistics = "sq";

    /// <summary>Whether a film is playing: the pages behind it do not move on meanwhile.</summary>
    public static bool Playing { get; private set; }

    // Automated checks go straight to what they look at.
    private static bool Automated => new[] { "MACHINES_SCREENSHOT", "MACHINES_MENU_SCREENSHOT", "MACHINES_MAP", "MACHINES_MP", "MACHINES_MENU_START" }
        .Any(name => System.Environment.GetEnvironmentVariable(name) is { Length: > 0 });

    /// <summary>Plays the films from one screen to the other; <paramref name="then"/> follows, at once when there are none.</summary>
    public static void Between(SceneTree tree, string? gameFolder, string from, string to, Action then)
    {
        var films = GameOptions.Transitions && !Automated ? Route(gameFolder, from, to) : new Queue<string>();
        Next();

        void Next()
        {
            Playing = films.Count > 0;
            if (Playing)
                FilmScreen.Play(tree, films.Dequeue(), Next);
            else
                then();
        }
    }

    /// <summary>The fewest films that lead from one screen to the other, in the order they play.</summary>
    private static Queue<string> Route(string? gameFolder, string from, string to)
    {
        var folder = gameFolder is null ? null : System.IO.Path.Combine(gameFolder, "flics");
        if (from == to || folder is null || !Directory.Exists(folder))
            return [];
        static string Name(string film) => System.IO.Path.GetFileNameWithoutExtension(film).ToLowerInvariant();
        var films = Directory.EnumerateFiles(folder, "*.smk").Where(f => Name(f) is [ 's', _, 's', _ ]).ToLookup(f => Name(f)[..2]);

        var cameBy = new Dictionary<string, string> { [from] = "" };
        var open = new Queue<string>([from]);
        while (!cameBy.ContainsKey(to) && open.TryDequeue(out var screen))
            foreach (var film in films[screen])
                if (cameBy.TryAdd(Name(film)[2..], film))
                    open.Enqueue(Name(film)[2..]);
        if (!cameBy.ContainsKey(to))
            return [];

        var way = new Stack<string>();
        for (var at = to; at != from; at = Name(cameBy[at])[..2])
            way.Push(cameBy[at]);
        return new Queue<string>(way);
    }
}
