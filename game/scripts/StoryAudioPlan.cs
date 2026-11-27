using System.Text.Json;
using Machines.Import.Model;
using NVector3 = System.Numerics.Vector3;

namespace Machines.Game;

// The story's sound, as rules without Godot (so the tests can hold them): which bus a line goes to, the order lines are
// spoken in, how afraid the music is, what each room of the deck sounds like and which loops stand in for the music no one
// has made yet. StoryAudio, StoryVoice, StorySounds and StoryMusic play what these decide.

/// <summary>Where a story line is heard.</summary>
public enum StoryVoiceChannel
{
    /// <summary>The ship's PA and its automatic voice: the Announcer bus (band-pass, hall) and the nearest loudspeakers.</summary>
    Announcer,
    /// <summary>The PA from the one loudspeaker beside the Locator ("Gefunden."): dry, in the world, no hall.</summary>
    Local,
    /// <summary>Ike and Bob over the radio: the Radio bus (band-pass, lo-fi), with a click before and after.</summary>
    Radio,
    /// <summary>The update whispering in the Locator's head: on the Voice bus, the world muffled while it speaks.</summary>
    Whisper,
    /// <summary>Text on the HUD only, never voiced (the P_HUD_ lines).</summary>
    Text,
    /// <summary>The Locator's own thoughts (P_L7_): on the Voice bus, close and dry, the world a few dB lower while they speak.</summary>
    Inner,
}

/// <summary>One voice file of a line in a language (game/assets/story/voice/manifest.json).</summary>
public sealed record StoryVoiceFile(string Line, string Lang, string File, double Seconds, string Preset, bool Placeholder);

/// <summary>
/// The voice files there are (game/assets/story/voice/manifest.json, written by tools/voice): per line and language its
/// file, length and robot-voice preset. A line or a language missing here is looked for by name all the same.
/// </summary>
public sealed class StoryVoiceManifest
{
    private readonly Dictionary<string, Dictionary<string, StoryVoiceFile>> _lines = new(StringComparer.Ordinal);

    public static StoryVoiceManifest Empty { get; } = new();

    public IEnumerable<string> Lines => _lines.Keys;

    public static StoryVoiceManifest Parse(string json)
    {
        var manifest = new StoryVoiceManifest();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Object)
            return manifest;
        foreach (var line in lines.EnumerateObject())
        {
            var languages = new Dictionary<string, StoryVoiceFile>(StringComparer.Ordinal);
            foreach (var lang in line.Value.EnumerateObject())
            {
                var entry = lang.Value;
                string Text(string key) => entry.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                var seconds = entry.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
                var placeholder = entry.TryGetProperty("placeholder", out var p) && p.ValueKind == JsonValueKind.True;
                languages[lang.Name] = new StoryVoiceFile(line.Name, lang.Name, Text("file") is { Length: > 0 } file ? file : $"{lang.Name}/{line.Name}.ogg",
                    seconds, Text("preset"), placeholder);
            }
            manifest._lines[line.Name] = languages;
        }
        return manifest;
    }

    /// <summary>The line's file in <paramref name="lang"/>, else in English, else null.</summary>
    public StoryVoiceFile? Find(string line, string lang) =>
        !_lines.TryGetValue(line, out var languages) ? null
        : languages.TryGetValue(lang, out var file) ? file
        : languages.GetValueOrDefault("en");

    /// <summary>The robot-voice preset the line was made with ("ship", "ike", ...), from any language; null if unknown.</summary>
    public string? Preset(string line) =>
        _lines.TryGetValue(line, out var languages) ? languages.Values.Select(f => f.Preset).FirstOrDefault(p => p.Length > 0) : null;
}

/// <summary>A line waiting to be spoken or speaking: <see cref="Seconds"/> is its length (or reading time when unvoiced).</summary>
public sealed record StoryQueuedLine(string Id, StoryVoiceChannel Channel, double Seconds, double Asked, bool Voiced = true)
{
    /// <summary>Before an announcement after a quiet while, the PA's gong: the voice starts this much later.</summary>
    public double Lead { get; init; }

    /// <summary>Whether the PA's gong comes first, should the PA have been quiet long enough (a cut fragment never has one).</summary>
    public bool Gong { get; init; }

    /// <summary>
    /// The seconds a line has waited while a line of the other kind spoke: the ship talking does not count against a radio
    /// line's <see cref="StoryLineQueue.RadioStale"/> (it lost its turn to the PA, its news is no older for that), nor a
    /// bark against an announcement's <see cref="StoryLineQueue.AnnouncementStale"/> (the alarm's four announcements give the
    /// barks a turn between them, and orders barked meanwhile must not cost the last one its turn).
    /// </summary>
    public double Held { get; init; }

    public bool Announcement => Channel is StoryVoiceChannel.Announcer or StoryVoiceChannel.Local or StoryVoiceChannel.Whisper or StoryVoiceChannel.Inner;

    /// <summary>Whether the line is the ship's or the update's (not the Locator's own thought): its end counts for the PA's quiet before a gong.</summary>
    public bool Loudspeaker => Announcement && Channel != StoryVoiceChannel.Inner;
}

/// <summary>What the line queue did.</summary>
public enum StoryQueueChange
{
    /// <summary>The line starts now (its voice after its <see cref="StoryQueuedLine.Lead"/>).</summary>
    Started,
    /// <summary>The line has been spoken to its end.</summary>
    Ended,
    /// <summary>The line was cut off while speaking: the Locator's own loudspeaker came over the radio, or the voice was cut.</summary>
    Interrupted,
    /// <summary>The line waited too long, or was cleared, and is never spoken.</summary>
    Dropped,
}

public readonly record struct StoryQueueEvent(StoryQueueChange Change, StoryQueuedLine Line);

/// <summary>
/// The order the story's lines are spoken in, one at a time: an announcement goes ahead of radio lines waiting and lines
/// of a kind keep their order, but the radio is not starved by the PA: after an announcement a radio line waiting has the
/// next turn, so the trio's barks come between the ship's announcements (the alarm's four would otherwise hold Ike's and
/// Bob's lines until they are stale). An announcement waits for a radio line speaking (barks are short); only the
/// loudspeaker beside the Locator ("Gefunden.", timed to its scare) cuts one off. A radio line that has waited longer than
/// <see cref="RadioStale"/>, not counting the time an announcement spoke meanwhile, is dropped (what Bob saw a while ago is
/// no news; that the ship talked over it does not make it old); an announcement after <see cref="AnnouncementStale"/>, not
/// counting the time barks spoke meanwhile. The PA's gong goes before an announcement
/// after <see cref="GongAfterQuiet"/> seconds of a quiet PA. Times are the caller's clock in seconds.
/// </summary>
public sealed class StoryLineQueue
{
    public const double RadioStale = 10, AnnouncementStale = 25, Gap = 0.35, GongLead = 1.1, GongAfterQuiet = 20;

    private readonly List<StoryQueuedLine> _waiting = [];
    private double _endsAt, _freeAt = double.NegativeInfinity, _lastAnnouncement = double.NegativeInfinity;

    // Whether the line spoken last was an announcement: then a radio line waiting goes next.
    private bool _radioTurn;

    /// <summary>The line speaking, or null.</summary>
    public StoryQueuedLine? Current { get; private set; }

    /// <summary>When the line speaking started.</summary>
    public double StartedAt { get; private set; }

    public IReadOnlyList<StoryQueuedLine> Waiting => _waiting;

    public List<StoryQueueEvent> Add(StoryQueuedLine line, double now)
    {
        var events = new List<StoryQueueEvent>();
        if (Current is { Announcement: false } radio && line.Channel == StoryVoiceChannel.Local)
        {
            Current = null;
            _radioTurn = false;
            events.Add(new(StoryQueueChange.Interrupted, radio));
        }
        // Announcements go ahead of the radio waiting, behind the announcements waiting.
        var at = line.Announcement ? _waiting.FindIndex(w => !w.Announcement) : -1;
        _waiting.Insert(at < 0 ? _waiting.Count : at, line);
        events.AddRange(Update(now));
        return events;
    }

    public List<StoryQueueEvent> Update(double now)
    {
        var events = new List<StoryQueueEvent>();
        if (Current is { } current && now >= _endsAt)
        {
            Leave(current, now);
            _freeAt = now + Gap;
            _radioTurn = current.Announcement;
            if (current.Loudspeaker)
                _lastAnnouncement = now;
            events.Add(new(StoryQueueChange.Ended, current));
        }
        while (Current is null && _waiting.Count > 0 && now >= _freeAt)
        {
            var at = _radioTurn ? Math.Max(0, _waiting.FindIndex(w => !w.Announcement)) : 0;
            var next = _waiting[at];
            _waiting.RemoveAt(at);
            if (now - next.Asked - next.Held > (next.Announcement ? AnnouncementStale : RadioStale))
            {
                events.Add(new(StoryQueueChange.Dropped, next));
                continue;
            }
            if (next is { Gong: true, Channel: StoryVoiceChannel.Announcer } && now - _lastAnnouncement > GongAfterQuiet)
                next = next with { Lead = GongLead };
            Current = next;
            StartedAt = now;
            _endsAt = now + next.Lead + next.Seconds;
            events.Add(new(StoryQueueChange.Started, next));
        }
        return events;
    }

    /// <summary>
    /// A line said at once, at the time a cutscene's script gives it (the automatic voice's "pod away" as pod 3 drops):
    /// an announcement or a thought speaking is cut off, and the lines waiting wait behind it. A radio bark speaking is
    /// short and is let finish (Ike's "In, in, in!" comes as the last of the trio boards, a moment before the launch); the
    /// line then goes next, after the usual gap, ahead of everything waiting.
    /// </summary>
    public List<StoryQueueEvent> Force(StoryQueuedLine line, double now)
    {
        var events = new List<StoryQueueEvent>();
        if (Current is { Announcement: false } && line.Channel != StoryVoiceChannel.Local)
        {
            _waiting.Insert(0, line);
            _radioTurn = false;
            return events;
        }
        if (Current is { } current)
        {
            Leave(current, now);
            if (current.Loudspeaker)
                _lastAnnouncement = now;
            events.Add(new(StoryQueueChange.Interrupted, current));
        }
        _radioTurn = false;
        Current = line;
        StartedAt = now;
        _endsAt = now + line.Lead + line.Seconds;
        events.Add(new(StoryQueueChange.Started, line));
        return events;
    }

    /// <summary>The lines of an id waiting are dropped (what they say is no longer true); the line speaking goes on.</summary>
    public List<StoryQueueEvent> Withdraw(string id)
    {
        var events = _waiting.Where(w => w.Id == id).Select(w => new StoryQueueEvent(StoryQueueChange.Dropped, w)).ToList();
        _waiting.RemoveAll(w => w.Id == id);
        return events;
    }

    /// <summary>
    /// The line speaking is cut off; with <paramref name="clear"/> every line waiting is dropped too. With
    /// <paramref name="onlyAnnouncements"/> only the ship and the Locator's head go silent (the PA breaking off as the core
    /// blows): a radio line speaking goes on and the radio lines waiting keep their turn.
    /// </summary>
    public List<StoryQueueEvent> Cut(double now, bool clear, bool onlyAnnouncements = false)
    {
        var events = new List<StoryQueueEvent>();
        if (Current is { } current && (current.Announcement || !onlyAnnouncements))
        {
            Leave(current, now);
            _freeAt = now + Gap;
            _radioTurn = current.Announcement;
            if (current.Loudspeaker)
                _lastAnnouncement = now;
            events.Add(new(StoryQueueChange.Interrupted, current));
        }
        if (clear)
        {
            var dropped = _waiting.Where(w => w.Announcement || !onlyAnnouncements).ToList();
            events.AddRange(dropped.Select(w => new StoryQueueEvent(StoryQueueChange.Dropped, w)));
            _waiting.RemoveAll(dropped.Contains);
        }
        return events;
    }

    // The line speaking stops: its time is booked to the lines of the other kind that waited through it, an announcement's
    // (its gong too) to the radio lines, a bark's to the announcements.
    private void Leave(StoryQueuedLine current, double now)
    {
        Current = null;
        for (var i = 0; i < _waiting.Count; i++)
            if (_waiting[i] is { } other && other.Announcement != current.Announcement)
                _waiting[i] = other with { Held = other.Held + Math.Max(0, Math.Min(now, _endsAt) - Math.Max(StartedAt, other.Asked)) };
    }
}

/// <summary>How afraid the story's music is: 0 calm, 1 uneasy, 2 hunted.</summary>
public sealed class StoryThreat
{
    /// <summary>Closer than this to the Thresher, or as suspected as this, is hunted; then uneasy.</summary>
    public const float HuntedWithin = 20, UneasyWithin = 50, HuntedSuspicion = 0.66f, UneasySuspicion = 0.25f;

    /// <summary>A lower level is held this long before the music calms down (it rises at once).</summary>
    public const double CalmAfter = 5;

    private double _lowerSince = double.NaN;

    public int Level { get; private set; }

    /// <summary>The director's level, which beats what the Thresher's distance says; null leaves it to that.</summary>
    public int? Override { get; set; }

    /// <summary>The level the Thresher's distance (metres, null when there is none) and the suspicion on the trio (0 to 1) ask for.</summary>
    public static int Raw(float? distance, float suspicion) =>
        distance < HuntedWithin || suspicion >= HuntedSuspicion ? 2
        : distance < UneasyWithin || suspicion >= UneasySuspicion ? 1
        : 0;

    /// <summary>Follows the level asked for at <paramref name="now"/>; true when <see cref="Level"/> changed.</summary>
    public bool Update(float? distance, float suspicion, double now)
    {
        var wanted = Math.Clamp(Override ?? Raw(distance, suspicion), 0, 2);
        if (wanted >= Level || Override is not null)
        {
            _lowerSince = double.NaN;
            var changed = wanted != Level;
            Level = wanted;
            return changed;
        }
        if (double.IsNaN(_lowerSince))
            _lowerSince = now;
        if (now - _lowerSince < CalmAfter)
            return false;
        _lowerSince = double.NaN;
        Level = wanted;
        return true;
    }
}

/// <summary>A loop playing: a sound effect of the story's (game/assets/story/sfx) at a level and pitch.</summary>
/// <param name="Every">For a pulse, the seconds between its hits (the sound played again and again); 0 for a loop.</param>
/// <param name="Threat">For a music layer, the threat level from which on it plays.</param>
public sealed record StoryLayer(string Sound, float Db, float Pitch = 1, float Every = 0, int Threat = 0);

/// <summary>A room's reverb (Godot's AudioEffectReverb values).</summary>
public sealed record StoryReverb(string Name, float RoomSize, float Damping, float Wet, float PredelayMs);

/// <summary>
/// What the deck sounds like where one is: each room's ambience loops (by its name in the deck's ZONES, else by its
/// section) and its reverb (by the room's height and size), and the music's cues. No cue's music has been made (the prologue
/// has its own score instead, <see cref="StoryScore"/>): a cue is a few of the ambience loops pitched down and layered by threat, until a file
/// <c>game/assets/story/music/&lt;cue&gt;.ogg</c> (and its stems <c>&lt;cue&gt;_1.ogg</c>, <c>&lt;cue&gt;_2.ogg</c>, which come in at
/// threat 1 and 2) takes its place.
/// </summary>
public static class StorySoundscape
{
    /// <summary>The cues of the prologue's music, in the order they come (docs/STORY.md, plan 5.3).</summary>
    public static readonly string[] Cues = ["wartung", "jagd", "gegenwehr", "absturz", "silence"];

    private static readonly StoryLayer[] Bays = [new("amb_dark_bay", -2), new("amb_ventilation", -14)];
    private static readonly StoryLayer[] Decks = [new("amb_dark_bay", -3), new("amb_metal_groans", -10), new("amb_reactor_hum", -14)];
    private static readonly StoryLayer[] Rooms = [new("amb_ventilation", -6), new("amb_reactor_hum", -12)];
    private static readonly StoryLayer[] Core = [new("amb_core_room", -2), new("amb_reactor_hum", -10)];
    private static readonly StoryLayer[] Corridors = [new("amb_reactor_hum", -6), new("amb_ventilation", -12), new("amb_metal_groans", -14)];
    private static readonly StoryLayer[] Ducts = [new("amb_duct_rumble", -4)];
    private static readonly StoryLayer[] Workshop = [new("amb_reactor_hum", -6), new("amb_ventilation", -12)];
    private static readonly StoryLayer[] Repair = [new("repair_hum", -6), new("amb_reactor_hum", -12)];
    private static readonly StoryLayer[] Notgang = [new("amb_fire", -6), new("amb_metal_groans", -6), new("amb_reentry", -14)];
    private static readonly StoryLayer[] PodBay = [new("amb_reentry", -6), new("amb_metal_groans", -8)];

    /// <summary>The ambience of a room of the deck by its name (R1, R4, V1b ...), else by the section it belongs to.</summary>
    public static IReadOnlyList<StoryLayer> Ambience(string? room, int section = 0) => room switch
    {
        "R0" or "R1" or "R2" or "R3" => Bays,
        "R4" or "R6" or "WL" or "EL" => Decks,
        "R5" or "R7" or "S" => Rooms,
        "R8" or "R9" => Core,
        "R10" or "R10a" or "P1" or "P1s" or "P2" or "R12" => Corridors,
        "K" or "V2" => Ducts,
        { } duct when duct.StartsWith("V1", StringComparison.Ordinal) => Ducts,
        "R13" or "R13a" => Workshop,
        "R14" => Repair,
        "R15" or "R15a" => Notgang,
        "R16" => PodBay,
        _ => section switch
        {
            1 => Bays,
            2 => Decks,
            3 => Core,
            4 or 5 => Corridors,
            6 => Workshop,
            7 => Notgang,
            _ => [],
        },
    };

    /// <summary>The reverb of a room of the deck: a duct is tight, a corridor short, a deck or the core a hall.</summary>
    public static StoryReverb Reverb(DeckZone? room)
    {
        if (room is null)
            return Room;
        var height = float.TryParse(room.Tag("height"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h) ? h : 6;
        var size = room.Max - room.Min;
        var narrow = MathF.Min(size.X, size.Y);
        if (height <= 4 || narrow <= 4)
            return Duct;
        if (height >= 28)
            return Cathedral;
        if (height >= 18 || size.X * size.Y >= 4800)
            return Hall;
        return narrow <= 20 ? Corridor : Room;
    }

    public static readonly StoryReverb Duct = new("duct", 0.2f, 0.25f, 0.28f, 8);
    public static readonly StoryReverb Corridor = new("corridor", 0.5f, 0.45f, 0.2f, 22);
    public static readonly StoryReverb Room = new("room", 0.45f, 0.55f, 0.15f, 15);
    public static readonly StoryReverb Hall = new("hall", 0.82f, 0.4f, 0.26f, 45);
    public static readonly StoryReverb Cathedral = new("core", 0.92f, 0.3f, 0.32f, 70);

    /// <summary>The room of the deck a point is in (x east, y north): the smallest room zone holding it, so a duct beats the deck around it.</summary>
    public static DeckZone? RoomAt(Deck? deck, System.Numerics.Vector2 at) =>
        deck?.Zones.Where(z => z.Tags.Contains("room") && z.Contains(at)).MinBy(z => (z.Max.X - z.Min.X) * (z.Max.Y - z.Min.Y));

    /// <summary>The section of the chapter a room belongs to (its <c>section=</c> tag), 0 if none.</summary>
    public static int Section(DeckZone? room) => int.TryParse(room?.Tag("section"), out var s) ? s : 0;

    /// <summary>The stand-in for a music cue: ambience loops pitched down into a drone, with layers for each threat level.</summary>
    public static IReadOnlyList<StoryLayer> MusicStandIn(string cue) => cue switch
    {
        "wartung" =>
        [
            new("amb_reactor_hum", -4, 0.5f),
            new("amb_ventilation", -16, 0.35f),
            new("amb_metal_groans", -8, 0.6f, Threat: 1),
            new("tracker_tick", -16, 0.5f, Every: 1.2f, Threat: 1),
            new("amb_duct_rumble", -6, 0.5f, Threat: 2),
        ],
        "jagd" =>
        [
            new("amb_duct_rumble", -4, 0.6f),
            new("amb_metal_groans", -8, 0.7f),
            new("amb_alarm_klaxon", -18, 0.5f, Threat: 2),
        ],
        "gegenwehr" =>
        [
            new("amb_reactor_hum", -4, 0.7f),
            new("amb_alarm_klaxon", -16, 0.5f, Threat: 1),
            new("amb_metal_groans", -8, 0.8f, Threat: 2),
        ],
        "absturz" =>
        [
            new("amb_reentry", -4, 0.6f),
            new("amb_metal_groans", -6, 0.5f, Threat: 1),
            new("amb_alarm_klaxon", -16, 0.55f, Threat: 2),
        ],
        _ => [],
    };

    /// <summary>Where a line is heard, by the preset its voice was made with, else by its id (P_ANN_, P_AUTO_, P_IKE_ ...).</summary>
    public static StoryVoiceChannel Channel(string line, string? preset) => preset switch
    {
        "ship" or "ship-old" or "auto" => StoryVoiceChannel.Announcer,
        "ship-dry" => StoryVoiceChannel.Local,
        "ike" or "bob" or "bob-radio" => StoryVoiceChannel.Radio,
        "whisper" => StoryVoiceChannel.Whisper,
        "l7" => StoryVoiceChannel.Inner,
        _ => line switch
        {
            "P_ANN_FOUND" => StoryVoiceChannel.Local,
            _ when line.StartsWith("P_ANN_", StringComparison.Ordinal) || line.StartsWith("P_AUTO_", StringComparison.Ordinal) => StoryVoiceChannel.Announcer,
            _ when line.StartsWith("P_IKE_", StringComparison.Ordinal) || line.StartsWith("P_BOB_", StringComparison.Ordinal) => StoryVoiceChannel.Radio,
            _ when line.StartsWith("P_WHISPER", StringComparison.Ordinal) => StoryVoiceChannel.Whisper,
            _ when line.StartsWith("P_L7_", StringComparison.Ordinal) => StoryVoiceChannel.Inner,
            _ => StoryVoiceChannel.Text,
        },
    };

    /// <summary>Whether the PA's gong goes before the line: the friendly PA's announcements, not the automatic voice, the old recording or a fragment.</summary>
    public static bool Gong(string line, string? preset) =>
        (preset ?? "ship") == "ship" && line.StartsWith("P_ANN_", StringComparison.Ordinal) && line != "P_ANN_FOUND"
        && !(line.StartsWith("P_ANN_C", StringComparison.Ordinal) && line.Length == 8 && char.IsDigit(line[7]) && line[7] >= '2');

    /// <summary>How long an unvoiced line stays up: its voice's length in the manifest, else a reading time.</summary>
    public const double UnvoicedSeconds = 3;

    /// <summary>The 1 to 3 loudspeakers nearest the listener within <paramref name="range"/> metres, nearest first.</summary>
    public static List<NVector3> NearestSpeakers(IEnumerable<NVector3> speakers, NVector3 listener, int most = 3, float range = 60) =>
        [.. speakers.Where(s => NVector3.Distance(s, listener) <= range).OrderBy(s => NVector3.DistanceSquared(s, listener)).Take(most)];

    /// <summary>How much later the n-th loudspeaker (0 nearest) speaks than the line starts, in seconds: 30 to 80 ms apart.</summary>
    public static double SpeakerDelay(int index) => 0.03 + 0.025 * index;
}

/// <summary>The story's sound effects (game/assets/story/sfx/manifest.json, written by tools/sfx): each one's file and whether it loops.</summary>
public sealed class StorySfxManifest
{
    private readonly SortedDictionary<string, (string File, bool Loop, double Seconds)> _sounds = new(StringComparer.Ordinal);

    public static StorySfxManifest Empty { get; } = new();

    public IEnumerable<string> Ids => _sounds.Keys;

    public static StorySfxManifest Parse(string json)
    {
        var manifest = new StorySfxManifest();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            return manifest;
        foreach (var sound in files.EnumerateObject())
        {
            var entry = sound.Value;
            var file = entry.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()! : sound.Name + ".ogg";
            var loop = entry.TryGetProperty("loop", out var l) && l.ValueKind == JsonValueKind.True;
            var seconds = entry.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
            manifest._sounds[sound.Name] = (file, loop, seconds);
        }
        return manifest;
    }

    public bool Has(string id) => _sounds.ContainsKey(id);

    public string? File(string id) => _sounds.TryGetValue(id, out var s) ? s.File : null;

    public bool Loops(string id) => _sounds.TryGetValue(id, out var s) && s.Loop;

    /// <summary>
    /// The sound to play for <paramref name="id"/>: itself, or one of its numbered variants (<c>thresher_step</c> picks one
    /// of <c>thresher_step_1</c> ... by <paramref name="pick"/>, given their count); null if neither exists.
    /// </summary>
    public string? Resolve(string id, Func<int, int> pick)
    {
        if (_sounds.ContainsKey(id))
            return id;
        var variants = _sounds.Keys.Where(k => k.Length > id.Length + 1 && k.StartsWith(id + "_", StringComparison.Ordinal) && k[(id.Length + 1)..].All(char.IsDigit)).ToList();
        return variants.Count == 0 ? null : variants[Math.Clamp(pick(variants.Count), 0, variants.Count - 1)];
    }

    /// <summary>How far a one-shot's pitch strays each time it plays, either way (user 2026-11-27: every sound effect a little different).</summary>
    public const float PitchVary = 0.04f;

    /// <summary><paramref name="pitch"/> strayed by up to <see cref="PitchVary"/> either way, <paramref name="unit"/> (0 to 1) saying where.</summary>
    public static float Varied(float pitch, float unit) => pitch * (1 + PitchVary * (2 * Math.Clamp(unit, 0, 1) - 1));
}

/// <summary>A piece of the story's score: its file (game/assets/story/music/&lt;name&gt;.ogg) and its length in seconds.</summary>
/// <remarks>
/// A piece with a hold (<see cref="HoldFrom"/> to <see cref="HoldTo"/>, bar lines measured from its file) loops that stretch
/// for as long as it is held (the boss, the escape): Godot's beat loop ends the stream at <see cref="HoldBeats"/> beats of
/// <see cref="HoldBpm"/>, which is <see cref="HoldTo"/>, and goes back to <see cref="HoldFrom"/>.
/// </remarks>
public sealed record StoryPiece(string Name, double Seconds, double HoldFrom = 0, double HoldTo = 0, int HoldBeats = 0)
{
    /// <summary>The piece loops a hold.</summary>
    public bool Holds => HoldTo > HoldFrom && HoldBeats > 0;

    /// <summary>The BPM that puts beat <see cref="HoldBeats"/> exactly at <see cref="HoldTo"/>.</summary>
    public double HoldBpm => Holds ? HoldBeats * 60 / HoldTo : 0;

    /// <summary>A place in the piece, wrapped into its hold once past the hold's end.</summary>
    public double IntoHold(double at) =>
        !Holds || at < HoldTo ? Math.Max(0, at) : HoldFrom + (at - HoldFrom) % (HoldTo - HoldFrom);
}

/// <summary>
/// Which of the score's lists plays as the background: the stealth's two pieces from the start until the break-in, the
/// old playlist after it, the armoury's fight piece while its waves run.
/// </summary>
public enum StoryList
{
    Stealth,
    Background,
    Waves,
}

/// <summary>What the story's score plays: the background playlist, the loud part (the break-in) or the armoury's boss's piece, the escape's piece, a staged rest of a few seconds, or nothing.</summary>
public enum StoryScorePart
{
    Playlist,
    Caught,
    Escape,
    Silence,
    /// <summary>A short staged silence (the 2 s after the armoury's boss falls); the playlist comes back after it.</summary>
    Rest,
}

/// <summary>Where the armoury stands for the music (stage 7, section 6): before or after the fight, a wave alive, a pause between waves, the boss.</summary>
public enum StoryArmouryMusic
{
    None,
    Wave,
    Pause,
    Boss,
}

/// <summary>What a director's cue (or a view's own word) tells the music, stage 7 (plan section 4, package M).</summary>
public enum StoryMusicCueKind
{
    /// <summary><c>thresher near &lt;m&gt;</c>: the Thresher is so many metres from the Locator.</summary>
    ThresherNear,
    /// <summary><c>thresher state &lt;calm|dwell|wary|draw|fire|lost&gt;</c>: what he is doing.</summary>
    ThresherState,
    /// <summary><c>wave &lt;n&gt; alive</c>: the armoury's wave n is on the floor.</summary>
    WaveAlive,
    /// <summary><c>wave pause</c>: the pause between two waves.</summary>
    WavePause,
    /// <summary><c>boss in</c>: gate N bursts and the big Grunt rolls in.</summary>
    BossIn,
    /// <summary><c>boss down</c>: the big Grunt is a wreck.</summary>
    BossDown,
    /// <summary><c>score escape &lt;s&gt;</c>: the lift bursts with s seconds left on the countdown (the escape piece's anchor).</summary>
    ScoreEscape,
    /// <summary><c>hush &lt;s&gt; &lt;dB&gt;</c>: the music dips by dB for s seconds and comes back (-60 dB and below is silence).</summary>
    Hush,
    /// <summary><c>launch</c>: the pod is away.</summary>
    Launch,
}

/// <summary>A cue the music reads: its kind, its number (metres, wave, seconds) and, for a hush, its dB; for a state, the state.</summary>
public readonly record struct StoryMusicCue(StoryMusicCueKind Kind, float Number = 0, float Db = 0, string? State = null)
{
    /// <summary>The Thresher's states the music knows (anything else is read as calm).</summary>
    public static readonly string[] States = ["calm", "dwell", "wary", "draw", "fire", "lost"];

    /// <summary>The cue in a director cue's words, or null when the music does not read it.</summary>
    public static StoryMusicCue? Parse(IReadOnlyList<string> words)
    {
        static float? N(string s) => float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) && float.IsFinite(n) ? n : null;
        return words switch
        {
            ["thresher", "near", var m] when N(m) is { } metres => new(StoryMusicCueKind.ThresherNear, Math.Max(0, metres)),
            ["thresher", "state", var state] => new(StoryMusicCueKind.ThresherState, State: States.Contains(state) ? state : "calm"),
            ["wave", var n, "alive"] when N(n) is { } wave => new(StoryMusicCueKind.WaveAlive, wave),
            ["wave", "pause"] => new(StoryMusicCueKind.WavePause),
            ["boss", "in"] => new(StoryMusicCueKind.BossIn),
            ["boss", "down"] => new(StoryMusicCueKind.BossDown),
            ["score", "escape", var s] when N(s) is { } left => new(StoryMusicCueKind.ScoreEscape, left),
            ["hush", var s, var db] when N(s) is { } seconds && N(db) is { } dip => new(StoryMusicCueKind.Hush, Math.Clamp(seconds, 0, 10), Math.Clamp(dip, -80, 0)),
            ["launch"] => new(StoryMusicCueKind.Launch),
            _ => null,
        };
    }
}

/// <summary>
/// The playlist under the Thresher while the trio sneaks (stage 7, section 2; design-stealth 9.3): ducked, never cut. He
/// beyond 120 m, or the Locator sheltered (a duct, R5, R7, the Leitstand), is -6 dB; his steps audible, -10 dB; within 40 m
/// or wary, -18 dB with a 1.2 kHz low-pass, reached in 2 s; locked on (draw) or firing, -24 dB. A deeper level comes at
/// once (faded over 2 s); a shallower one only 8 s after the deeper one last held, and then over 6 s. The two staged
/// silences (the window's pause, the slam of H) are hushes (<see cref="StoryMusicCueKind.Hush"/>), not this.
/// </summary>
public sealed class StoryDuck
{
    public const float FarDb = -6, StepsDb = -10, NearDb = -18, LockDb = -24, FilterHz = 1200, OpenHz = 20000;

    /// <summary>Metres: beyond this his steps are not heard; within this he is near.</summary>
    public const float StepsWithin = 120, NearWithin = 40;

    /// <summary>Seconds: a shallower level waits this long after the deeper one last held, then the playlist comes back over this long; down it goes over this long.</summary>
    public const double CalmAfter = 8, BackOver = 6, DownOver = 2;

    private double _deepSince = double.NegativeInfinity;

    /// <summary>The level held now (dB).</summary>
    public float Db { get; private set; } = FarDb;

    /// <summary>The low-pass is on (near, wary, locked on or firing).</summary>
    public bool Filter => Db <= NearDb;

    /// <summary>The level the Thresher at <paramref name="distance"/> (null: none on the deck) in <paramref name="state"/> asks for, the Locator <paramref name="sheltered"/> or not.</summary>
    public static float Raw(float? distance, string? state, bool sheltered) =>
        state is "draw" or "fire" ? LockDb
        : distance < NearWithin || state == "wary" ? NearDb
        : sheltered || distance is null || distance > StepsWithin || state == "dwell" ? FarDb
        : StepsDb;

    /// <summary>Follows the level asked for at <paramref name="now"/> (seconds); the level held.</summary>
    public float Update(float? distance, string? state, bool sheltered, double now)
    {
        var wanted = Raw(distance, state, sheltered);
        if (wanted <= Db)
        {
            Db = wanted;
            _deepSince = now;
        }
        else if (now - _deepSince >= CalmAfter)
        {
            Db = wanted;
            _deepSince = now;
        }
        return Db;
    }

    /// <summary>How fast (dB a second) the playlist moves from <paramref name="from"/> to the held level: down over 2 s, up over 6 s, never slower than 3 dB/s down.</summary>
    public static float Speed(float from, float to) =>
        to < from ? MathF.Max(3, (from - to) / (float)DownOver) : MathF.Max(0.5f, (to - from) / (float)BackOver);

    /// <summary>Whether a room shelters the Locator from him (he cannot come in): a duct, R5, R7 or the Leitstand (A1).</summary>
    public static bool Sheltered(Machines.Import.Model.DeckZone? room) =>
        room is not null && (room.Name is "R5" or "R7" or "A1" || StorySoundscape.Reverb(room) == StorySoundscape.Duct);
}

/// <summary>
/// The prologue's score, the user's own pieces (game/assets/story/music, made from their MP3s with ffmpeg and levelled to
/// one loudness, as its README says). From the start the stealth's background, Silent Circuit v1 and v2, each after the
/// other (with a short overlap) and round again, never restarted by a section or a checkpoint (<see cref="Start"/>); under
/// the Thresher (section 2) it is ducked (<see cref="StoryDuck"/>). At the break-in (Erwischt) it is cut for good and the
/// loud part of Lost In The Dark plays once from its entry; when that has ended and the trio is out of the core chamber
/// (section 5), the old playlist follows, Lost In The Void, Lost Vessel, Drift Into The Black and the calm part of Lost In The
/// Dark, a little quieter. In the armoury (section 6) Static Dread v2 takes the playlist's place while the waves run (a
/// wave alive at the after-caught level, the pauses at -10 dB); the boss bursts in to Static Dread v1, which loops its body
/// until he falls; his fall is 2 s of silence, then the playlist again where it was. At the Notgang (section 7) the
/// playlist fades out for good; Circuitry v1 starts from its beginning as the lift bursts (<see cref="EscapeFrom"/>), plays
/// through and loops its last section (<see cref="HoldFrom"/> to <see cref="HoldTo"/>) until the launch; it fades out over
/// the first seconds of the pod's cutscene (StoryAudio.MusicOut). View only: nothing here touches the sim; each machine
/// plays it from the shared beats.
/// </summary>
public sealed class StoryScore(int checkpoint = 0)
{
    /// <summary>The first checkpoint after the break-in (7 Erwischt): a chapter begun there has no stealth left.</summary>
    public const int BrokenInFrom = 7;

    /// <summary>The stealth's background from the start until the break-in: Silent Circuit v1 and v2, one after the other and round again.</summary>
    public static readonly StoryPiece[] Stealth =
    [
        new("silent_circuit_v1", 156.00),
        new("silent_circuit_v2", 168.02),
    ];

    /// <summary>The background after the break-in, in order.</summary>
    public static readonly StoryPiece[] Playlist =
    [
        new("lost_in_the_void", 147.65),
        new("lost_vessel", 148.52),
        new("drift_into_the_black", 239.02),
        new("lost_in_the_dark_calm", 156.20),
    ];

    /// <summary>The armoury's fights before the boss: Static Dread v2, round again from its start should the waves outlast it.</summary>
    public static readonly StoryPiece[] Waves = [new("static_dread_v2", 229.01)];

    /// <summary>The loud part of Lost In The Dark, from its entry (157.2 s into the original) to its own fade: the break-in.</summary>
    public static readonly StoryPiece Caught = new("lost_in_the_dark_loud", 61.90);

    /// <summary>
    /// The armoury's boss: Static Dread v1, looping its loud body until he falls. Measured from the file (onset envelope,
    /// see the music README): 70 BPM, the first beat at 0.056 s, bars of four; the hold is bars 8–48, from 27.485 s, where
    /// the body comes in after the intro, to 164.627 s, where it breaks down before the end (40 bars, 137.14 s).
    /// </summary>
    public static readonly StoryPiece Boss = new("static_dread_v1", 183.92, 27.485, 164.627, 192);

    /// <summary>
    /// The escape: Circuitry v1. Measured from the file: 173.7 BPM, the first beat at 0.049 s, bars of four; the hold is
    /// bars 100–132, from 138.219 s (in the piece's last third) to 182.433 s, where its last loud bar gives way to the fade
    /// (32 bars, 44.21 s).
    /// </summary>
    public static readonly StoryPiece Escape = new("circuitry_v1", 187.00, 138.219, 182.433, 528);

    /// <summary>Every piece of the score.</summary>
    public static IEnumerable<StoryPiece> Pieces => [.. Stealth, .. Playlist, .. Waves, Caught, Boss, Escape];

    /// <summary>A list's pieces.</summary>
    public static StoryPiece[] Of(StoryList list) => list switch
    {
        StoryList.Stealth => Stealth,
        StoryList.Waves => Waves,
        _ => Playlist,
    };

    /// <summary>
    /// Seconds: the overlap of one playlist piece's end with the next one's start; the playlist's cut at the break-in; its
    /// fade back in afterwards (and after a checkpoint); the playlist's fade out at the Notgang.
    /// </summary>
    public const double Overlap = 2, CutFade = 0.3, ResumeFade = 4, EscapeFade = 2;

    /// <summary>
    /// The playlist's level in dB after the trio was caught (sections 5 and on), and its dips while uneasy and hunted, so the
    /// Thresher's steps and the stings come through.
    /// </summary>
    public const float AfterCaughtDb = -3, UneasyDb = -1.5f, HuntedDb = -4;

    /// <summary>The playlist's level (dB) while the Locator boots in its clamp: softly under its thoughts, up to its own level once free.</summary>
    public const float BootDb = -9;

    /// <summary>The playlist's level (dB) in the armoury's pauses: the lift's hum and the batch bar's tick carry the quiet.</summary>
    public const float PauseDb = -10;

    /// <summary>Seconds of silence when the armoury's boss falls, before the playlist comes back.</summary>
    public const double BossRest = 2;

    /// <summary>The escape's hold (<see cref="Escape"/>): bars 100 to 132 of Circuitry v1.</summary>
    public static double HoldFrom => Escape.HoldFrom;

    /// <summary>The end of the escape's hold.</summary>
    public static double HoldTo => Escape.HoldTo;

    /// <summary>Beats from the file's start to <see cref="HoldTo"/>, for Godot's beat loop (it loops at BeatCount beats at its BPM).</summary>
    public static int HoldBeats => Escape.HoldBeats;

    /// <summary>The BPM that puts beat <see cref="HoldBeats"/> exactly at <see cref="HoldTo"/> (173.65 against the measured 173.7).</summary>
    public static double HoldBpm => Escape.HoldBpm;

    /// <summary>Seconds from the countdown's start to the lift's burst (T-354 of 360): the escape's piece waits for the burst.</summary>
    public const double BurstDelay = 6;

    /// <summary>Seconds the music waits past <see cref="BurstDelay"/> for the burst's cue before it starts by the countdown alone.</summary>
    public const double BurstGrace = 2;

    /// <summary>
    /// Where the escape's piece starts at a retry from the escape's later checkpoints: where a typical run hears it there (354 - the
    /// checkpoint's seconds left: 13 Galerie with 170, 14 Bucht with 120; the six minutes since the Kühlschlange). From the Notgang (12) it starts with the burst.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, double> RetryAt = new Dictionary<int, double> { [13] = 184, [14] = 234 };

    private bool _caughtEnded, _brokenIn = checkpoint >= BrokenInFrom;
    private double _restUntil = double.NegativeInfinity;
    private double? _burstAt;
    private float? _countFrom;

    public StoryScorePart Part { get; private set; } = StoryScorePart.Playlist;

    /// <summary>The armoury's state for the playlist's level.</summary>
    public StoryArmouryMusic Armoury { get; private set; }

    /// <summary>The break-in: the loud part starts (true), unless the escape has begun or the score is over.</summary>
    public bool BreakIn()
    {
        if (Part is StoryScorePart.Escape or StoryScorePart.Silence)
            return false;
        Part = StoryScorePart.Caught;
        _caughtEnded = false;
        _brokenIn = true;
        return true;
    }

    /// <summary>
    /// The background list that plays now: Static Dread v2 while the armoury's waves run (a wave alive or a pause), the
    /// stealth's pieces until the break-in, the old playlist after it.
    /// </summary>
    public StoryList List =>
        Armoury is StoryArmouryMusic.Wave or StoryArmouryMusic.Pause ? StoryList.Waves
        : _brokenIn ? StoryList.Background
        : StoryList.Stealth;

    /// <summary>The loud part has played to its end.</summary>
    public void CaughtEnded() => _caughtEnded = true;

    /// <summary>The score falls silent for good (the chapter is over).</summary>
    public void Stop() => Part = StoryScorePart.Silence;

    /// <summary>The armoury's wave is alive: the playlist holds its after-caught level, the threat's dips off.</summary>
    public void Wave()
    {
        if (Armoury != StoryArmouryMusic.Boss)
            Armoury = StoryArmouryMusic.Wave;
    }

    /// <summary>A pause between the armoury's waves: the playlist drops to <see cref="PauseDb"/>.</summary>
    public void Pause()
    {
        if (Armoury != StoryArmouryMusic.Boss)
            Armoury = StoryArmouryMusic.Pause;
    }

    /// <summary>The boss bursts in: Static Dread v1 over the cut playlist (true), unless it plays already or the escape has begun.</summary>
    public bool BossIn()
    {
        if (Armoury == StoryArmouryMusic.Boss || Part is not StoryScorePart.Playlist)
            return false;
        Armoury = StoryArmouryMusic.Boss;
        return BreakIn();
    }

    /// <summary>The boss falls at <paramref name="now"/>: 2 s of silence (true while the boss was on), then the playlist at the armoury's after level.</summary>
    public bool BossDown(double now)
    {
        if (Armoury != StoryArmouryMusic.Boss || Part is StoryScorePart.Escape or StoryScorePart.Silence)
            return false;
        Armoury = StoryArmouryMusic.None;
        Part = StoryScorePart.Rest;
        _restUntil = now + BossRest;
        return true;
    }

    /// <summary>The lift bursts at <paramref name="now"/> (the cue <c>score escape</c>): the escape piece's anchor.</summary>
    public void Burst(double now)
    {
        if (Part != StoryScorePart.Silence)
            _burstAt ??= now;
    }

    /// <summary>
    /// Four times a second, from the section the trio is in, the seconds left on the crash countdown (null while it is not
    /// running) and the clock: what should play now. From the Notgang (section 7) or the countdown on it is the escape, kept
    /// after the launch stops the countdown; the playlist comes back after the loud part once that has ended and the trio is
    /// out of the core chamber, and after the boss's rest.
    /// </summary>
    public StoryScorePart Follow(int section, float? countdownLeft, double now = 0)
    {
        if (Part == StoryScorePart.Silence)
            return Part;
        if (countdownLeft is not null || section >= 7 || _burstAt is not null)
        {
            Part = StoryScorePart.Escape;
            Armoury = StoryArmouryMusic.None;
        }
        else if (Part == StoryScorePart.Caught && _caughtEnded && section >= 5)
            Part = StoryScorePart.Playlist;
        else if (Part == StoryScorePart.Rest && now >= _restUntil)
            Part = StoryScorePart.Playlist;
        return Part;
    }

    /// <summary>
    /// In the escape, where its piece starts now (seconds into it), or null while it waits for the lift's burst: from the burst's
    /// cue (<see cref="Burst"/>), else by the countdown, <see cref="BurstDelay"/> after it began (and <see cref="BurstGrace"/>
    /// more for the cue); at once from a later checkpoint (<see cref="RetryAt"/>). Late starts begin as far in as they are late.
    /// </summary>
    public double? EscapeFrom(int checkpoint, float? countdownLeft, double now)
    {
        if (Part != StoryScorePart.Escape)
            return null;
        if (_burstAt is { } burst)
            return EscapeOffset(checkpoint, now - burst);
        if (countdownLeft is not { } left)
            return null;
        _countFrom ??= left;
        var run = _countFrom.Value - left;
        if (RetryAt.ContainsKey(checkpoint))
            return EscapeOffset(checkpoint, run);
        return run >= BurstDelay + BurstGrace ? EscapeOffset(checkpoint, run - BurstDelay) : null;
    }

    /// <summary>Where the escape's piece is <paramref name="since"/> seconds after the burst in a chapter begun at <paramref name="checkpoint"/>: from its start (or a retry's place), into the hold once past its end.</summary>
    public static double EscapeOffset(int checkpoint, double since) =>
        IntoHold((RetryAt.TryGetValue(checkpoint, out var at) ? at : 0) + Math.Max(0, since));

    /// <summary>A place in the escape's piece, wrapped into the hold (<see cref="HoldFrom"/> to <see cref="HoldTo"/>) once past its end.</summary>
    public static double IntoHold(double at) => Escape.IntoHold(at);

    /// <summary>
    /// The playlist's level (dB) in a section at a threat level (0 calm, 1 uneasy, 2 hunted), and while the Locator boots;
    /// in the armoury by its state (a wave or the boss: the after-caught level without the threat's dips; a pause: -10 dB);
    /// under the Thresher (<paramref name="duck"/>, section 2) the duck's level in place of the threat's dips.
    /// </summary>
    public static float PlaylistDb(int section, int threat, bool booting = false, StoryArmouryMusic armoury = StoryArmouryMusic.None, float? duck = null) =>
        armoury switch
        {
            StoryArmouryMusic.Wave or StoryArmouryMusic.Boss => AfterCaughtDb,
            StoryArmouryMusic.Pause => PauseDb,
            _ => duck ?? (section >= 5 ? AfterCaughtDb : 0) + threat switch { >= 2 => HuntedDb, 1 => UneasyDb, _ => 0 },
        } + (booting ? BootDb : 0);

    /// <summary>A list's piece after <paramref name="track"/>: the next, or the first again after the last.</summary>
    public static int Next(StoryList list, int track) => (track + 1) % Of(list).Length;

    /// <summary>
    /// Where a list starts in a chapter begun at <paramref name="checkpoint"/>: from the beginning at checkpoint 0; else
    /// where it was when the chapter was played last in this session (<paramref name="remembered"/>: a checkpoint loaded
    /// after a fall goes on with the piece that played); else at the piece a run usually hears there (the stealth's second
    /// piece at the antechamber, every other list from its first).
    /// </summary>
    public static (int Track, double At) Start(StoryList list, int checkpoint, (int Track, double At)? remembered)
    {
        if (checkpoint <= 0)
            return (0, 0);
        if (remembered is { } was && was.Track >= 0 && was.Track < Of(list).Length)
            return (was.Track, Math.Max(0, was.At));
        return (list == StoryList.Stealth && checkpoint == 6 ? 1 : 0, 0);
    }
}
