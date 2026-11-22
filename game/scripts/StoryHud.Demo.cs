using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// MACHINES_STORY_HUD puts the story's HUD into states for pictures, before the director, the hunters and the Grunts' orders
/// exist to bring them: a comma list of boot (held 80.6 s into the boot, or boot:&lt;seconds&gt;), sneak (an objective with its way, a signature, two
/// contacts on the tracker), grunts (one drawing the hunters off, one down), coop (a person plays Ike), alarm (and spotted), countdown, dead (the
/// Locator lost), subtitles (two lines held on the screen) and prompt (hack and hold). Only what it names is changed; the
/// rest stays as the match has it. With MACHINES_STORY=prolog and MACHINES_SCREENSHOT_STORY to set the trio down, e.g.
/// MACHINES_STORY=prolog MACHINES_SCREENSHOT_STORY=27,27,90 MACHINES_STORY_HUD=sneak,grunts,subtitles.
/// </summary>
public partial class StoryHud
{
    private readonly Demo? _demo;

    private sealed class Demo(IReadOnlySet<string> parts)
    {
        /// <summary>The demo's states, from MACHINES_STORY_HUD; null when it is not set.</summary>
        public static Demo? FromEnvironment() => System.Environment.GetEnvironmentVariable("MACHINES_STORY_HUD") is { Length: > 0 } list
            ? new Demo(list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(p => p.ToLowerInvariant()).ToHashSet())
            : null;

        private bool Has(string part) => parts.Contains(part);

        /// <summary>
        /// The boot held at one moment: "boot" 80 s in (the update refused, most lines up), "boot:&lt;seconds&gt;" at that moment.
        /// </summary>
        public double? BootAt => Has("boot") ? 80.6
            : parts.FirstOrDefault(p => p.StartsWith("boot:", StringComparison.Ordinal)) is { } at
              && double.TryParse(at[5..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : null;

        private bool _ordered;

        public StoryReadout Apply(StoryReadout now, GameWorld world, StoryHud hud)
        {
            var hero = world.HeroOf(Game.Me);
            if (Has("sneak") && hero is not null)
            {
                // The objective as the director will set it: a place in the deck and the way there.
                var place = now.ObjectivePlace ?? StoryMarks.Locate(world.Deck, "hack_s");
                var route = hud._route;
                var way = place is { } goal && route is not null ? route.Chain(hero.Position, goal) : [];
                var ahead = Forward(hero);
                now = now with
                {
                    Objective = now.Objective ?? "Reach maintenance deck A",
                    ObjectivePlace = place,
                    ObjectiveDistance = place is { } at ? System.Numerics.Vector2.Distance(at, hero.Position) : null,
                    Waypoints = way,
                    WayLength = way.Count > 0 ? route!.Steps(hero.Position) : now.WayLength,
                    Signature = 0.34f,
                    Contacts =
                    [
                        Contact(hero, ahead, 0.5f, 13),
                        Contact(hero, ahead, -2.2f, 27),
                    ],
                };
            }
            if (Has("grunts") && now.Grunts.Count >= 2)
            {
                now = now with
                {
                    Grunts =
                    [
                        now.Grunts[0] with { Doing = GruntDoing.Distracting, Health = 0.64f, Distance = 18 },
                        now.Grunts[1] with { Doing = GruntDoing.Down, Health = 0, Distance = 7 },
                    ],
                };
                if (!_ordered)
                {
                    _ordered = true;
                    hud.OrderGiven(KeyMap.Act.GruntDistract);
                }
            }
            // In co-op a person plays Ike: the card's P, and the label over it without the orders' word.
            if (Has("coop") && now.Grunts.Count >= 1)
                now = now with { Grunts = [now.Grunts[0] with { Person = true }, .. now.Grunts.Skip(1)] };
            if (Has("alarm"))
                now = now with { Alarm = true, Spotted = true, Signature = 0.92f };
            if (Has("countdown"))
                now = now with { CountdownLeft = 94, CountdownShare = 94 / 180f };
            if (Has("prompt"))
                now = now with { Prompts = [new StoryPrompt(StoryPromptKind.Hack, "terminal_bob", 1.2f), new StoryPrompt(StoryPromptKind.Hold, "switch_g1", 2.4f)] };
            if (Has("dead"))
                now = now with { Ending = StoryEnding.LocatorLost, Health = 0 };
            if (Has("subtitles") && hud._hud.Subtitles is { Speaking: false } subtitles)
            {
                subtitles.Say(StorySpeaker.Ship, "Maintenance unit L-7. Please proceed to bay four for your update.", 600);
                subtitles.Say(StorySpeaker.Ike, "Ike? Ike. I like it. Who are we knocking over?", 600);
            }
            return now;
        }

        // Which way the hero faces, on the deck.
        private static System.Numerics.Vector2 Forward(Machine hero) => new(MathF.Cos(hero.Facing), MathF.Sin(hero.Facing));

        // Something moving at a bearing from the hero's facing (positive to the right) and a distance.
        private static TrackerContact Contact(Machine hero, System.Numerics.Vector2 ahead, float bearing, float distance)
        {
            var (sin, cos) = MathF.SinCos(-bearing);
            var way = new System.Numerics.Vector2(ahead.X * cos - ahead.Y * sin, ahead.X * sin + ahead.Y * cos);
            return new TrackerContact(hero.Position + way * distance, bearing, distance);
        }
    }
}
