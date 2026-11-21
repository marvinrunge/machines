using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>
/// What the story's HUD reads off a <see cref="StoryRun"/>, by the names it keeps them under in its
/// <see cref="StoryRun.Flags"/> and <see cref="StoryRun.Timers"/>: the objective and where it lies, the alarm, the countdown,
/// whether the trio is spotted, which Grunt is down or drawing the hunters off, and which place is done with (a door hacked, the
/// weapon taken). The director sets them on its ticks, so they are in the checksum like the rest of the run; the HUD only reads.
/// </summary>
public static class StoryMarks
{
    /// <summary>Set while the ship's alarm sounds (from the break at 90 % on).</summary>
    public const string Alarm = "alarm";

    /// <summary>Set while a hunter sees the trio: the HUD says SIGNATURE DETECTED.</summary>
    public const string Spotted = "spotted";

    /// <summary>The timer of the countdown to the crash: the game time it runs out.</summary>
    public const string Countdown = "countdown";

    /// <summary>The timer of the whole countdown's start, for the share of it left: the game time it began.</summary>
    public const string CountdownFrom = "countdown_from";

    /// <summary>Set while the Locator's HUD boots in its clamp at the chapter's start (Erwachen): the plates come one by one.</summary>
    public const string Boot = "boot";

    private const string ObjectivePrefix = "objective:", DownPrefix = "down:", DistractingPrefix = "distracting:", DormantPrefix = "dormant:", DonePrefix = "done:";

    /// <summary>The flag of a seat's Grunt lying as a scrap heap until the Locator puts it together.</summary>
    public static string Down(PlayerId seat) => DownPrefix + seat.Value;

    /// <summary>The flag of a seat's Grunt drawing the hunters off.</summary>
    public static string Distracting(PlayerId seat) => DistractingPrefix + seat.Value;

    /// <summary>The flag of a seat's Grunt still hanging switched off in its clamp, before the Locator hacks it awake.</summary>
    public static string Dormant(PlayerId seat) => DormantPrefix + seat.Value;

    /// <summary>The flag of a place of the deck (a marker's name) that is done with: hacked, taken, used.</summary>
    public static string Done(string marker) => DonePrefix + marker;

    /// <summary>
    /// Sets what the trio is to do: <paramref name="text"/> (English, the HUD translates it) and where, a marker's or a zone's
    /// name of the deck or "x,y" in metres; null <paramref name="text"/> takes the objective away. There is one at a time.
    /// </summary>
    public static void SetObjective(StoryRun run, string? text, string? place = null)
    {
        run.Flags.RemoveWhere(flag => flag.StartsWith(ObjectivePrefix, StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(text))
            run.Flags.Add(ObjectivePrefix + text + (place is { Length: > 0 } ? "@" + place : ""));
    }

    /// <summary>The objective the run has, its text and the name of its place; null without one.</summary>
    public static (string Text, string? Place)? Objective(StoryRun run)
    {
        foreach (var flag in run.Flags)
        {
            if (!flag.StartsWith(ObjectivePrefix, StringComparison.Ordinal))
                continue;
            var body = flag[ObjectivePrefix.Length..];
            var at = body.LastIndexOf('@');
            return at < 0 ? (body, null) : (body[..at], body[(at + 1)..]);
        }
        return null;
    }

    /// <summary>Where a place named as <see cref="SetObjective"/> takes it lies on the deck: a marker, the middle of a zone, or "x,y"; null when the deck has none of it.</summary>
    public static Vector2? Locate(Deck? deck, string? place)
    {
        if (string.IsNullOrWhiteSpace(place))
            return null;
        if (place.Split(',') is [var x, var y]
            && float.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var east)
            && float.TryParse(y, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var north))
            return new Vector2(east, north);
        if (deck is null)
            return null;
        if (deck.Markers.FirstOrDefault(m => m.Name == place) is { } marker)
            return marker.Position;
        return deck.Zone(place) is { } zone ? (zone.Min + zone.Max) / 2 : null;
    }
}
