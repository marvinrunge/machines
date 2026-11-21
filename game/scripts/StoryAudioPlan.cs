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

    public bool Announcement => Channel is StoryVoiceChannel.Announcer or StoryVoiceChannel.Local or StoryVoiceChannel.Whisper;
}

/// <summary>What the line queue did.</summary>
public enum StoryQueueChange
{
    /// <summary>The line starts now (its voice after its <see cref="StoryQueuedLine.Lead"/>).</summary>
    Started,
    /// <summary>The line has been spoken to its end.</summary>
    Ended,
    /// <summary>The line was cut off while speaking: an announcement came over the radio, or the voice was cut.</summary>
    Interrupted,
    /// <summary>The line waited too long, or was cleared, and is never spoken.</summary>
    Dropped,
}

public readonly record struct StoryQueueEvent(StoryQueueChange Change, StoryQueuedLine Line);

/// <summary>
/// The order the story's lines are spoken in, one at a time: an announcement beats the radio (it cuts off a radio line
/// speaking, and goes ahead of radio lines waiting), lines of a kind keep their order, and a radio line that has waited
/// longer than <see cref="RadioStale"/> is dropped (what Bob saw a while ago is no news). The PA's gong goes before an
/// announcement after <see cref="GongAfterQuiet"/> seconds of a quiet PA. Times are the caller's clock in seconds.
/// </summary>
public sealed class StoryLineQueue
{
    public const double RadioStale = 10, AnnouncementStale = 25, Gap = 0.35, GongLead = 1.1, GongAfterQuiet = 20;

    private readonly List<StoryQueuedLine> _waiting = [];
    private double _endsAt, _freeAt = double.NegativeInfinity, _lastAnnouncement = double.NegativeInfinity;

    /// <summary>The line speaking, or null.</summary>
    public StoryQueuedLine? Current { get; private set; }

    /// <summary>When the line speaking started.</summary>
    public double StartedAt { get; private set; }

    public IReadOnlyList<StoryQueuedLine> Waiting => _waiting;

    public List<StoryQueueEvent> Add(StoryQueuedLine line, double now)
    {
        var events = new List<StoryQueueEvent>();
        if (Current is { Announcement: false } radio && line.Announcement)
        {
            Current = null;
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
            Current = null;
            _freeAt = now + Gap;
            if (current.Announcement)
                _lastAnnouncement = now;
            events.Add(new(StoryQueueChange.Ended, current));
        }
        while (Current is null && _waiting.Count > 0 && now >= _freeAt)
        {
            var next = _waiting[0];
            _waiting.RemoveAt(0);
            if (now - next.Asked > (next.Announcement ? AnnouncementStale : RadioStale))
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

    /// <summary>The line speaking is cut off; with <paramref name="clear"/> every line waiting is dropped too.</summary>
    public List<StoryQueueEvent> Cut(double now, bool clear)
    {
        var events = new List<StoryQueueEvent>();
        if (Current is { } current)
        {
            Current = null;
            _freeAt = now + Gap;
            if (current.Announcement)
                _lastAnnouncement = now;
            events.Add(new(StoryQueueChange.Interrupted, current));
        }
        if (clear)
        {
            events.AddRange(_waiting.Select(w => new StoryQueueEvent(StoryQueueChange.Dropped, w)));
            _waiting.Clear();
        }
        return events;
    }
}

/// <summary>How afraid the story's music is: 0 calm, 1 uneasy, 2 hunted.</summary>
public sealed class StoryThreat
{
    /// <summary>Closer than this to the Thresher, or as suspected as this, is hunted; then uneasy.</summary>
    public const float HuntedWithin = 14, UneasyWithin = 36, HuntedSuspicion = 0.66f, UneasySuspicion = 0.25f;

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
/// section) and its reverb (by the room's height and size), and the music's cues. No music has been made (ElevenLabs
/// refused it): a cue is a few of the ambience loops pitched down and layered by threat, until a file
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
        if (height >= 14)
            return Cathedral;
        if (height >= 9 || size.X * size.Y >= 1200)
            return Hall;
        return narrow <= 10 ? Corridor : Room;
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
        _ => line switch
        {
            "P_ANN_FOUND" => StoryVoiceChannel.Local,
            _ when line.StartsWith("P_ANN_", StringComparison.Ordinal) || line.StartsWith("P_AUTO_", StringComparison.Ordinal) => StoryVoiceChannel.Announcer,
            _ when line.StartsWith("P_IKE_", StringComparison.Ordinal) || line.StartsWith("P_BOB_", StringComparison.Ordinal) => StoryVoiceChannel.Radio,
            _ when line.StartsWith("P_WHISPER", StringComparison.Ordinal) => StoryVoiceChannel.Whisper,
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
