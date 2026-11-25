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
/// (played on a story deck), <c>part &lt;name&gt; outside</c> (on any other map) or <c>part &lt;name&gt; space</c> (in space,
/// away from any map: see below), each with its takes (<c>take &lt;name&gt; &lt;seconds&gt; &lt;kind&gt; [&lt;subject&gt;]
/// [key=value ...]</c>, one after the other) and cues (<c>at &lt;seconds&gt; &lt;cue&gt; [args ...] [key=value ...]</c>,
/// seconds from the part's start).
/// </para>
/// <para>
/// A space part is filmed on a stage of its own (<c>SpaceStage</c>: the stars, a planet, a sun; no map, no
/// machines), so it plays wherever the map part it goes with plays: the deck or outside part before it, or, when it comes
/// first, the one after it. <see cref="Part"/> gives that map part with its space parts as one, in the file's order.
/// Its points are metres in space (x, y across, up), nothing under them; its part line may set the stage
/// (<c>planet=x,y,up radius= face=yaw,pitch sun=yaw,pitch</c>).
/// </para>
/// <para>
/// Points (<see cref="CutscenePoint"/>): a name (a deck marker or camera, a prop, a machine's role, <c>camera</c>), a name
/// lifted <c>name@3</c>, a point on a prop <c>name@x,y,z</c> (metres in the model's own frame, moving with it), or
/// <c>x,y,up</c> in metres (x east, y north, up over the ground); <c>a|b</c> takes the first that is there.
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
        ["sound"] = (1, 1),       // a sound (game/assets/story/sfx/<name>.ogg); at=point, db=, for= seconds, pitch= (0.5 an octave lower, and slower), from= seconds into the file
        ["shake"] = (3, 3),       // point degrees seconds (CameraRig.Shake)
        ["rumble"] = (1, 1),      // degrees (CameraRig.Rumble); ease=seconds
        ["tilt"] = (2, 2),        // roll pitch degrees (Game.Tilt); sway=degrees, ease=seconds
        ["glitch"] = (2, 2),      // amount seconds (FilmLook glitch)
        ["fade"] = (2, 2),        // to seconds: to black (1) or back (0)
        ["explosion"] = (2, 2),   // point size
        ["sparks"] = (1, 1),      // point; toward=point, amount=
        ["shot"] = (3, 3),        // weapon from to: a round that only flies (no sim behind it); speed=
        ["fire"] = (3, 3),        // point width height: flames left burning (on a prop's point: burning on it as it moves)
        ["smoke"] = (2, 2),       // point size: a column of smoke left rising (on a prop's point: streaming from it as it moves); rise=, early= seconds already risen
        ["bowshock"] = (2, 2),    // prop strength: the glowing shock before its nose as it meets the air, easing to the strength (0 gone); ease=, size=
        ["impact"] = (2, 2),      // point size: something huge striking the ground (in space: the planet's face there): flash, ring, rising dust
        ["flash"] = (2, 2),       // point size: a blinding flash and its light; light=range, colour=r,g,b
        ["light"] = (2, 2),       // point range: a lamp (no shadows) shining down on the point from above= metres over it, for the rest of the cutscene; energy=, angle=, colour=r,g,b
        ["prop"] = (5, 5),        // name model from to seconds: a model of the scene moved; ease=, glow=, trail=, scale=, yaw=, nose=, engines= strength of its drives (Effects.Engines), lean= degrees tipped into its way, land=, sink= metres driven into the ground, until=
        ["opening"] = (2, 2),     // point radius: a round hole in the deck's floor there, the stars below showing through it and what falls through it seen falling on; above= metres over the floor (a cradle's base)
        ["silence"] = (1, 1),     // seconds: every sound of the match fades to nothing
        ["music"] = (1, 1),       // a track (game/assets/<name>.ogg: music/remake/deep_drift_v2), the story's music making way; from= seconds into it, fade= seconds coming in, db=
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

    /// <summary>How many dB the match's sounds (the story's World bus) are taken down while it plays (its <c>world</c> line); 0 leaves them.</summary>
    public float World { get; }
    public IReadOnlyList<CutscenePart> Parts { get; }

    private CutsceneScript(string name, float bars, float blend, float world, IReadOnlyList<CutscenePart> parts)
    {
        Name = name;
        Bars = bars;
        Blend = blend;
        World = world;
        Parts = parts;
    }

    /// <summary>
    /// The part played on a deck (<paramref name="onDeck"/>) or outside, or null when it has none: with the space parts that
    /// go with it (each with the map part before it, or the one after it when there is none before) as one part in the
    /// file's order, their takes and cues marked <see cref="CutsceneTake.Space"/>, their cues' seconds counted from the
    /// start of the whole.
    /// </summary>
    public CutscenePart? Part(bool onDeck)
    {
        var index = Parts.ToList().FindIndex(p => !p.Space && p.OnDeck == onDeck);
        if (index < 0)
            return null;
        var run = Parts.Where((p, i) => i == index || (p.Space && Owner(i) == index)).ToList();
        if (run.Count == 1)
            return run[0];
        var takes = new List<CutsceneTake>();
        var cues = new List<CutsceneCue>();
        var options = new Dictionary<string, string>();
        foreach (var part in run)
        {
            var start = takes.Sum(t => t.Seconds);
            takes.AddRange(part.Takes.Select(t => t with { Space = part.Space }));
            cues.AddRange(part.Cues.Select(c => c with { At = c.At + start, Space = part.Space }));
            foreach (var (key, value) in part.Options)
                options.TryAdd(key, value);
        }
        var main = Parts[index];
        return new CutscenePart(main.Name, main.OnDeck, takes, cues.OrderBy(c => c.At).ToList()) { Options = options };
    }

    // The map part a space part goes with: the one before it, else the one after it.
    private int Owner(int space)
    {
        for (var i = space - 1; i >= 0; i--)
            if (!Parts[i].Space)
                return i;
        for (var i = space + 1; i < Parts.Count; i++)
            if (!Parts[i].Space)
                return i;
        return -1;
    }

    public static CutsceneScript Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    public static CutsceneScript Parse(string text, string file = "cutscene")
    {
        string? name = null;
        var bars = 0.12f;
        var blend = 0.5f;
        var world = 0f;
        var parts = new List<CutscenePart>();
        (string Name, string Where, Dictionary<string, string> Options, List<CutsceneTake> Takes, List<CutsceneCue> Cues)? part = null;
        void Close()
        {
            if (part is { } p)
                parts.Add(new CutscenePart(p.Name, p.Where == "deck", p.Takes, p.Cues.OrderBy(c => c.At).ToList())
                {
                    Space = p.Where == "space",
                    Options = p.Options,
                });
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
                case "world":
                    world = Number(positional.FirstOrDefault() ?? throw Wrong("world needs decibels"));
                    break;
                case "part":
                    Close();
                    if (positional is not [var partName, var where and ("deck" or "outside" or "space")])
                        throw Wrong("a part is 'part <name> deck|outside|space'");
                    part = (partName, where, options, [], []);
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
                    if (p.Where == "space" && kind is not ("fixed" or "chase"))
                        throw Wrong($"a take in space is fixed or chase, not {kind}");
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
                    throw Wrong($"'{words[0]}' is not a cutscene line (cutscene, bars, blend, world, part, take, at)");
            }
        }
        Close();
        if (name is null)
            throw new FormatException($"{file}: no 'cutscene <name>' line");
        if (parts.Count == 0 || parts.Any(p => p.Takes.Count == 0))
            throw new FormatException($"{file}: every part needs a take");
        if (parts.All(p => p.Space))
            throw new FormatException($"{file}: a space part goes with a deck or outside part");
        return new CutsceneScript(name, bars, blend, world, parts);
    }
}

/// <summary>
/// Part of a cutscene, played on a deck or outside (or in <see cref="Space"/>, with one of those): its takes one after the
/// other, and its cues by time.
/// </summary>
public sealed record CutscenePart(string Name, bool OnDeck, IReadOnlyList<CutsceneTake> Takes, IReadOnlyList<CutsceneCue> Cues)
{
    /// <summary>A part in space, on its own stage (<c>SpaceStage</c>), played with a map part.</summary>
    public bool Space { get; init; }

    /// <summary>The part line's key=value words (a space part's stage).</summary>
    public IReadOnlyDictionary<string, string> Options { get; init; } = new Dictionary<string, string>();

    /// <summary>Whether any of its takes is filmed in space.</summary>
    public bool HasSpace => Space || Takes.Any(t => t.Space);

    /// <summary>Seconds, all its takes together.</summary>
    public float Length => Takes.Sum(t => t.Seconds);

    /// <summary>When a take starts, in seconds from the part's start.</summary>
    public float StartOf(int take) => Takes.Take(take).Sum(t => t.Seconds);
}

/// <summary>A take: a camera of a kind (see <see cref="CutsceneScript.Kinds"/>) on a subject for so many seconds.</summary>
public sealed record CutsceneTake(string Name, float Seconds, string Kind, string? Subject, IReadOnlyDictionary<string, string> Options, int Line)
{
    /// <summary>Filmed in space (<c>SpaceStage</c>), not on the map.</summary>
    public bool Space { get; init; }

    public string? Text(string key) => Options.GetValueOrDefault(key);

    public float Number(string key, float otherwise) =>
        Options.TryGetValue(key, out var text) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : otherwise;
}

/// <summary>A cue at a moment of its part (see <see cref="CutsceneScript.Cues"/>).</summary>
public sealed record CutsceneCue(float At, string Kind, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Options, int Line)
{
    /// <summary>Set off in space (<c>SpaceStage</c>): its points are metres in space, its models and effects there.</summary>
    public bool Space { get; init; }

    public string? Text(string key) => Options.GetValueOrDefault(key);

    public float Number(int arg) => float.Parse(Args[arg], NumberStyles.Float, CultureInfo.InvariantCulture);

    public float Number(string key, float otherwise) =>
        Options.TryGetValue(key, out var text) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : otherwise;
}

/// <summary>
/// A point a cutscene names: a name (<see cref="Name"/>, with <see cref="Up"/> metres on top) to look up in the scene, or
/// metres on the map (x east, y north, <see cref="Up"/> over the ground). <see cref="Local"/>: a point on a prop, X, Y and
/// Up being metres along the model's own x, y and z, moving and turning with it.
/// </summary>
public readonly record struct CutscenePoint(string? Name, float X, float Y, float Up, bool Local = false)
{
    /// <summary>The alternatives of <c>a|b|...</c>, each <c>name</c>, <c>name@up</c>, <c>name@x,y,z</c> or <c>x,y,up</c>.</summary>
    public static IReadOnlyList<CutscenePoint> Parse(string text) => text.Split('|').Select(One).ToList();

    private static CutscenePoint One(string text)
    {
        float Number(string word) => float.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value : throw new FormatException($"'{text}' is no point ('{word}' is not a number)");
        var at = text.IndexOf('@');
        var numbers = (at < 0 ? text : text[(at + 1)..]).Split(',');
        if (numbers.Length is not (1 or 3))
            throw new FormatException($"'{text}' is no point (x,y,up has three numbers)");
        if (at < 0)
            return numbers is [var x, var y, var up] ? new CutscenePoint(null, Number(x), Number(y), Number(up)) : new CutscenePoint(text, 0, 0, 0);
        var name = text[..at];
        return numbers is [var lx, var ly, var lz]
            ? new CutscenePoint(name, Number(lx), Number(ly), Number(lz), Local: true)
            : new CutscenePoint(name, 0, 0, Number(numbers[0]));
    }
}
