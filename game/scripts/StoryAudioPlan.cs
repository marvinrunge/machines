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
    /// The seconds a radio line has waited while an announcement spoke: the ship talking does not count against its
    /// <see cref="StoryLineQueue.RadioStale"/> (it lost its turn to the PA, its news is no older for that).
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
/// no news; that the ship talked over it does not make it old). The PA's gong goes before an announcement
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
    /// A line said at once, at the time a cutscene's script gives it (the announcement Erwischt turns on is timed to the core
    /// blowing): whatever speaks is cut off, and the lines waiting wait behind it.
    /// </summary>
    public List<StoryQueueEvent> Force(StoryQueuedLine line, double now)
    {
        var events = new List<StoryQueueEvent>();
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

    // The line speaking stops: an announcement's time (its gong too) is booked to the radio lines that waited through it.
    private void Leave(StoryQueuedLine current, double now)
    {
        Current = null;
        if (!current.Announcement)
            return;
        for (var i = 0; i < _waiting.Count; i++)
            if (_waiting[i] is { Announcement: false } radio)
                _waiting[i] = radio with { Held = radio.Held + Math.Max(0, Math.Min(now, _endsAt) - Math.Max(StartedAt, radio.Asked)) };
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
            new("thresher_step_1", -10, 0.6f, Every: 0.9f, Threat: 2),
        ],
        "jagd" =>
        [
            new("amb_duct_rumble", -4, 0.6f),
            new("amb_metal_groans", -8, 0.7f),
            new("thresher_step_2", -9, 0.55f, Every: 0.75f, Threat: 1),
            new("amb_alarm_klaxon", -18, 0.5f, Threat: 2),
        ],
        "gegenwehr" =>
        [
            new("amb_reactor_hum", -4, 0.7f),
            new("thresher_step_1", -9, 0.8f, Every: 0.5f),
            new("amb_alarm_klaxon", -16, 0.5f, Threat: 1),
            new("amb_metal_groans", -8, 0.8f, Threat: 2),
        ],
        "absturz" =>
        [
            new("amb_reentry", -4, 0.6f),
            new("amb_metal_groans", -6, 0.5f, Threat: 1),
            new("thresher_step_3", -9, 0.5f, Every: 0.6f, Threat: 2),
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
}

/// <summary>A piece of the story's score: its file (game/assets/story/music/&lt;name&gt;.ogg) and its length in seconds.</summary>
public sealed record StoryPiece(string Name, double Seconds);

/// <summary>What the story's score plays: the background playlist, the loud part as the trio is caught, the escape's piece, or nothing.</summary>
public enum StoryScorePart
{
    Playlist,
    Caught,
    Escape,
    Silence,
}

/// <summary>
/// The prologue's score, the user's own pieces (game/assets/story/music, made from their MP3s with ffmpeg and levelled to
/// one loudness, as its README says): from the start a playlist in the background, Lost In The Void, Lost Vessel, Drift Into The Black and the
/// calm part of Lost In The Dark, each after the other (with a short overlap) and round again from the first should it run
/// out, through sections 1–3 and the hack, never restarted by a section or a checkpoint (<see cref="Start"/>). At the
/// break-in (Erwischt) the playlist is cut and the loud part of Lost In The Dark plays once from its entry; when it has
/// ended and the trio is out of the core chamber (section 5), the playlist goes on where it was, a little quieter. From the
/// crash countdown on, Pressure At Sector Nine plays, started so that its end lands where the countdown runs out
/// (<see cref="EscapeOffset"/>); the pod's launch keeps it, and it fades out over the first seconds of the
/// pod's cutscene (StoryAudio.MusicOut). The threat lowers the
/// playlist a little (<see cref="PlaylistDb"/>); the stand-ins' drones never play over it. View only: nothing here touches
/// the sim; each machine plays it from the shared beats.
/// </summary>
public sealed class StoryScore
{
    /// <summary>The background, in order.</summary>
    public static readonly StoryPiece[] Playlist =
    [
        new("lost_in_the_void", 147.65),
        new("lost_vessel", 148.52),
        new("drift_into_the_black", 239.02),
        new("lost_in_the_dark_calm", 156.20),
    ];

    /// <summary>The loud part of Lost In The Dark, from its entry (157.2 s into the original) to its own fade.</summary>
    public static readonly StoryPiece Caught = new("lost_in_the_dark_loud", 61.90);

    /// <summary>Pressure At Sector Nine, its last silence trimmed.</summary>
    public static readonly StoryPiece Escape = new("pressure_at_sector_nine", 179.90);

    /// <summary>Every piece of the score.</summary>
    public static IEnumerable<StoryPiece> Pieces => [.. Playlist, Caught, Escape];

    /// <summary>
    /// Seconds: the overlap of one playlist piece's end with the next one's start; the playlist's cut at the break-in; its
    /// fade back in afterwards (and after a checkpoint); the playlist's fade out under the escape's piece.
    /// </summary>
    public const double Overlap = 2, CutFade = 0.3, ResumeFade = 4, EscapeFade = 1.5;

    /// <summary>
    /// The playlist's level in dB after the trio was caught (sections 5 and on), and its dips while uneasy and hunted, so the
    /// Thresher's steps and the stings come through.
    /// </summary>
    public const float AfterCaughtDb = -3, UneasyDb = -1.5f, HuntedDb = -4;

    /// <summary>The playlist's level (dB) while the Locator boots in its clamp: softly under its thoughts, up to its own level once free.</summary>
    public const float BootDb = -9;

    private bool _caughtEnded;

    public StoryScorePart Part { get; private set; } = StoryScorePart.Playlist;

    /// <summary>The break-in: the loud part starts (true), unless the escape has begun or the score is over.</summary>
    public bool BreakIn()
    {
        if (Part is StoryScorePart.Escape or StoryScorePart.Silence)
            return false;
        Part = StoryScorePart.Caught;
        _caughtEnded = false;
        return true;
    }

    /// <summary>The loud part has played to its end.</summary>
    public void CaughtEnded() => _caughtEnded = true;

    /// <summary>The score falls silent for good (the chapter is over).</summary>
    public void Stop() => Part = StoryScorePart.Silence;

    /// <summary>
    /// Four times a second, from the section the trio is in and the seconds left on the crash countdown (null while it is
    /// not running): what should play now. The escape's piece starts with the countdown and is kept after the launch stops
    /// it; the playlist comes back after the loud part once that has ended and the trio is out of the core chamber.
    /// </summary>
    public StoryScorePart Follow(int section, float? countdownLeft)
    {
        if (Part == StoryScorePart.Silence)
            return Part;
        if (countdownLeft is not null)
            Part = StoryScorePart.Escape;
        else if (Part == StoryScorePart.Caught && _caughtEnded && section >= 5)
            Part = StoryScorePart.Playlist;
        return Part;
    }

    /// <summary>
    /// Where the escape's piece starts with <paramref name="left"/> seconds on the countdown: so that it ends as the countdown
    /// runs out (at 180 s from its start, its last note a moment before zero; at a checkpoint's 150 s, 29.9 s in).
    /// </summary>
    public static double EscapeOffset(float left, double length) => Math.Clamp(length - left, 0, Math.Max(0, length - 1));

    /// <summary>The playlist's level (dB) in a section at a threat level (0 calm, 1 uneasy, 2 hunted), and while the Locator boots.</summary>
    public static float PlaylistDb(int section, int threat, bool booting = false) =>
        (section >= 5 ? AfterCaughtDb : 0) + threat switch { >= 2 => HuntedDb, 1 => UneasyDb, _ => 0 } + (booting ? BootDb : 0);

    /// <summary>The playlist's piece after <paramref name="track"/>: the next, or the first again after the last.</summary>
    public static int Next(int track) => (track + 1) % Playlist.Length;

    /// <summary>
    /// Where the playlist starts in a chapter begun at <paramref name="checkpoint"/>: from the beginning at checkpoint 0;
    /// else where it was when the chapter was played last in this session (<paramref name="remembered"/>: a checkpoint loaded
    /// after a fall goes on with the piece that played); else at the piece a run usually hears there (the start of Lost
    /// Vessel at the antechamber, of Drift Into The Black from the break-in on).
    /// </summary>
    public static (int Track, double At) Start(int checkpoint, (int Track, double At)? remembered)
    {
        if (checkpoint <= 0)
            return (0, 0);
        if (remembered is { } was && was.Track >= 0 && was.Track < Playlist.Length)
            return (was.Track, Math.Max(0, was.At));
        return (checkpoint switch { 1 => 0, 2 => 1, _ => 2 }, 0);
    }
}
