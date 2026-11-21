using System.Globalization;

namespace Machines.Game;

/// <summary>
/// A cutscene as data (<c>data/planets/&lt;chapter&gt;/&lt;name&gt;.cut</c>, the remake's own): its takes, each a camera of a
/// kind, and its cues, what is seen and heard at their moments. A <see cref="Cutscene"/> plays it live in a match, and a
/// film (<see cref="Cinematic"/>, <c>MACHINES_CINEMATIC=prolog-&lt;name&gt;</c>) films it the same way. Godot-free, so the
/// tests read the scripts. It only films and sounds: what changes the world (machines spawned, doors opened, the core
/// destroyed) is the story director's, on ticks, so a skipped cutscene leaves the world as a watched one.
/// <para>
/// One line each; <c>//</c> starts a comment. <c>cutscene &lt;name&gt;</c>; <c>bars &lt;share&gt;</c> (the black bars' height,
/// 0.12) and <c>blend &lt;seconds&gt;</c> (back to the player's view, 0.5); then one or more parts, <c>part &lt;name&gt; deck</c>
/// (played on a story deck) or <c>part &lt;name&gt; outside</c> (on any other map), each with its takes
/// (<c>take &lt;name&gt; &lt;seconds&gt; &lt;kind&gt; [&lt;subject&gt;] [key=value ...]</c>, one after the other) and cues
/// (<c>at &lt;seconds&gt; &lt;cue&gt; [args ...] [key=value ...]</c>, seconds from the part's start).
/// </para>
/// <para>
/// Points (<see cref="CutscenePoint"/>): a name (a deck marker or camera, a prop, a machine's role, <c>camera</c>), a name
/// lifted <c>name@3</c>, or <c>x,y,up</c> in metres (x east, y north, up over the ground); <c>a|b</c> takes the first that
/// is there.
/// </para>
/// </summary>
public sealed class CutsceneScript
{
    /// <summary>The camera kinds of a take, and the subject each wants (null: none).</summary>
    public static readonly IReadOnlyDictionary<string, string?> Kinds = new Dictionary<string, string?>
    {
        // From inside a machine (subject: who); look= turns the view there.
        ["cockpit"] = "who",
        // Standing at a deck camera (subject: its name), looking along it or at look=.
        ["cam"] = "camera",
        // Along a deck rail (subject: its name), looking along it or at look=.
        ["rail"] = "rail",
        // Standing at a point (subject), looking at look=.
        ["fixed"] = "point",
        // Riding beside a prop (subject) at offset=x,y,z metres, looking at it.
        ["chase"] = "prop",
    };

    /// <summary>The cues, and how many arguments each takes before its key=value options (at least, at most).</summary>
    public static readonly IReadOnlyDictionary<string, (int Least, int Most)> Cues = new Dictionary<string, (int, int)>
    {
        ["line"] = (1, 1),        // a voice line by its id (game/assets/story/voice)
        ["sound"] = (1, 1),       // a sound (game/assets/story/sfx/<name>.ogg); at=point, db=
        ["shake"] = (3, 3),       // point degrees seconds (CameraRig.Shake)
        ["rumble"] = (1, 1),      // degrees (CameraRig.Rumble); ease=seconds
        ["tilt"] = (2, 2),        // roll pitch degrees (Game.Tilt); sway=degrees, ease=seconds
        ["glitch"] = (2, 2),      // amount seconds (FilmLook glitch)
        ["fade"] = (2, 2),        // to seconds: to black (1) or back (0)
        ["explosion"] = (2, 2),   // point size
        ["sparks"] = (1, 1),      // point; toward=point, amount=
        ["shot"] = (3, 3),        // weapon from to: a round that only flies (no sim behind it); speed=
        ["fire"] = (3, 3),        // point width height: flames left burning
        ["smoke"] = (2, 2),       // point size: a column of smoke left rising
        ["flash"] = (2, 2),       // point size: a blinding flash and its light; light=range, colour=r,g,b
        ["prop"] = (5, 5),        // name model from to seconds: a model of the scene moved; ease=, glow=, trail=, scale=, yaw=, nose=, land=, until=
        ["silence"] = (1, 1),     // seconds: every sound of the match fades to nothing
        ["dusk"] = (1, 1),        // share 0 to 1: the day dims towards night (PlanetLook.ForcedNight); dim= darkens the picture besides (PlanetLook.Dim)
        ["event"] = (1, 8),       // words for the story (lights, doors ...), passed on
    };

    /// <summary>The roles a point or a cockpit can name: the machines of that type (and subtype) nearest the camera.</summary>
    public static readonly IReadOnlyDictionary<string, (string Type, string? Sub)> Roles = new Dictionary<string, (string, string?)>
    {
        ["commander"] = ("ADMINISTRATOR", "COMMANDER"),
        ["reaper"] = ("AGGRESSOR", "ASSASSIN"),
        ["grunt"] = ("AGGRESSOR", "GRUNT"),
    };

    /// <summary>
    /// Names a point or a cockpit can take that are not of the deck: the camera itself, the Locator (the story's first
    /// seat, else the player's machine), and Ike and Bob (the second and third seats).
    /// </summary>
    public static readonly IReadOnlySet<string> Special = new HashSet<string> { "camera", "locator", "ike", "bob" };

    public string Name { get; }
    public float Bars { get; }
    public float Blend { get; }
    public IReadOnlyList<CutscenePart> Parts { get; }

    private CutsceneScript(string name, float bars, float blend, IReadOnlyList<CutscenePart> parts)
    {
        Name = name;
        Bars = bars;
        Blend = blend;
        Parts = parts;
    }

    /// <summary>The part played on a deck (<paramref name="onDeck"/>) or outside, or null when it has none.</summary>
    public CutscenePart? Part(bool onDeck) => Parts.FirstOrDefault(p => p.OnDeck == onDeck);

    public static CutsceneScript Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    public static CutsceneScript Parse(string text, string file = "cutscene")
    {
        string? name = null;
        var bars = 0.12f;
        var blend = 0.5f;
        var parts = new List<CutscenePart>();
        (string Name, bool Deck, List<CutsceneTake> Takes, List<CutsceneCue> Cues)? part = null;
        void Close()
        {
            if (part is { } p)
                parts.Add(new CutscenePart(p.Name, p.Deck, p.Takes, p.Cues.OrderBy(c => c.At).ToList()));
            part = null;
        }
        var number = 0;
        foreach (var raw in text.Split('\n'))
        {
            number++;
            var comment = raw.IndexOf("//", StringComparison.Ordinal);
            var words = (comment >= 0 ? raw[..comment] : raw).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                continue;
            FormatException Wrong(string why) => new($"{file}:{number}: {why}");
            var positional = words.Skip(1).Where(w => !w.Contains('=')).ToList();
            var options = new Dictionary<string, string>();
            foreach (var pair in words.Skip(1).Where(w => w.Contains('=')).Select(w => w.Split('=', 2)))
                if (!options.TryAdd(pair[0], pair[1]))
                    throw Wrong($"'{pair[0]}=' twice");
            float Number(string word) => float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value : throw Wrong($"'{word}' is not a number");
            switch (words[0])
            {
                case "cutscene":
                    name = positional.FirstOrDefault() ?? throw Wrong("a cutscene needs a name");
                    break;
                case "bars":
                    bars = Number(positional.FirstOrDefault() ?? throw Wrong("bars needs a share"));
                    break;
                case "blend":
                    blend = Number(positional.FirstOrDefault() ?? throw Wrong("blend needs seconds"));
                    break;
                case "part":
                    Close();
                    if (positional is not [var partName, var where and ("deck" or "outside")])
                        throw Wrong("a part is 'part <name> deck|outside'");
                    part = (partName, where == "deck", [], []);
                    break;
                case "take":
                {
                    if (part is not { } p)
                        throw Wrong("a take outside a part");
                    if (positional.Count < 3)
                        throw Wrong("a take is 'take <name> <seconds> <kind> [<subject>] [key=value ...]'");
                    var kind = positional[2];
                    if (!Kinds.TryGetValue(kind, out var subject))
                        throw Wrong($"no camera kind '{kind}' (there are {string.Join(", ", Kinds.Keys)})");
                    var seconds = Number(positional[1]);
                    if (seconds <= 0)
                        throw Wrong("a take lasts more than 0 seconds");
                    if (subject is not null && positional.Count < 4)
                        throw Wrong($"a {kind} take names its {subject}");
                    p.Takes.Add(new CutsceneTake(positional[0], seconds, kind, positional.ElementAtOrDefault(3), options, number));
                    break;
                }
                case "at":
                {
                    if (part is not { } p)
                        throw Wrong("a cue outside a part");
                    if (positional.Count < 2)
                        throw Wrong("a cue is 'at <seconds> <cue> [args ...] [key=value ...]'");
                    var kind = positional[1];
                    if (!Cues.TryGetValue(kind, out var count))
                        throw Wrong($"no cue '{kind}' (there are {string.Join(", ", Cues.Keys)})");
                    var args = positional.Skip(2).ToList();
                    if (args.Count < count.Least || args.Count > count.Most)
                        throw Wrong($"the cue '{kind}' takes {count.Least}{(count.Most != count.Least ? $" to {count.Most}" : "")} arguments, not {args.Count}");
                    p.Cues.Add(new CutsceneCue(Number(positional[0]), kind, args, options, number));
                    break;
                }
                default:
                    throw Wrong($"'{words[0]}' is not a cutscene line (cutscene, bars, blend, part, take, at)");
            }
        }
        Close();
        if (name is null)
            throw new FormatException($"{file}: no 'cutscene <name>' line");
        if (parts.Count == 0 || parts.Any(p => p.Takes.Count == 0))
            throw new FormatException($"{file}: every part needs a take");
        return new CutsceneScript(name, bars, blend, parts);
    }
}

/// <summary>Part of a cutscene, played on a deck or outside: its takes one after the other, and its cues by time.</summary>
public sealed record CutscenePart(string Name, bool OnDeck, IReadOnlyList<CutsceneTake> Takes, IReadOnlyList<CutsceneCue> Cues)
{
    /// <summary>Seconds, all its takes together.</summary>
    public float Length => Takes.Sum(t => t.Seconds);

    /// <summary>When a take starts, in seconds from the part's start.</summary>
    public float StartOf(int take) => Takes.Take(take).Sum(t => t.Seconds);
}

/// <summary>A take: a camera of a kind (see <see cref="CutsceneScript.Kinds"/>) on a subject for so many seconds.</summary>
public sealed record CutsceneTake(string Name, float Seconds, string Kind, string? Subject, IReadOnlyDictionary<string, string> Options, int Line)
{
    public string? Text(string key) => Options.GetValueOrDefault(key);

    public float Number(string key, float otherwise) =>
        Options.TryGetValue(key, out var text) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : otherwise;
}

/// <summary>A cue at a moment of its part (see <see cref="CutsceneScript.Cues"/>).</summary>
public sealed record CutsceneCue(float At, string Kind, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Options, int Line)
{
    public string? Text(string key) => Options.GetValueOrDefault(key);

    public float Number(int arg) => float.Parse(Args[arg], NumberStyles.Float, CultureInfo.InvariantCulture);

    public float Number(string key, float otherwise) =>
        Options.TryGetValue(key, out var text) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : otherwise;
}

/// <summary>
/// A point a cutscene names: a name (<see cref="Name"/>, with <see cref="Up"/> metres on top) to look up in the scene, or
/// metres on the map (x east, y north, <see cref="Up"/> over the ground).
/// </summary>
public readonly record struct CutscenePoint(string? Name, float X, float Y, float Up)
{
    /// <summary>The alternatives of <c>a|b|...</c>, each <c>name</c>, <c>name@up</c> or <c>x,y,up</c>.</summary>
    public static IReadOnlyList<CutscenePoint> Parse(string text) => text.Split('|').Select(One).ToList();

    private static CutscenePoint One(string text)
    {
        float Number(string word) => float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : throw new FormatException($"'{text}' is no point ('{word}' is not a number)");
        if (text.Split(',') is [var x, var y, var up])
            return new CutscenePoint(null, Number(x), Number(y), Number(up));
        if (text.Split(',').Length > 1)
            throw new FormatException($"'{text}' is no point (x,y,up has three numbers)");
        var at = text.IndexOf('@');
        return at < 0 ? new CutscenePoint(text, 0, 0, 0) : new CutscenePoint(text[..at], 0, 0, Number(text[(at + 1)..]));
    }
}
