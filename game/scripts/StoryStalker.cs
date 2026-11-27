using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>What the Thresher is doing as the view shows and plays it (design-stealth §4), read from the sim's seeker.</summary>
public enum StalkerState
{
    /// <summary>On his round, walking: plants, a wide swaying beam.</summary>
    Calm,
    /// <summary>Standing at a point of his round, at his guard post or in a scripted look: no plants, a slow sweep.</summary>
    Dwell,
    /// <summary>Something caught his eye or ear (suspicion at least <see cref="StorySight.WaryAt"/>), or a lure draws him: halt, ping, the rod.</summary>
    Wary,
    /// <summary>Sure, drawing on a hero in sight (<see cref="StorySeeker.Draw"/>, 0.6 s): the lock whine, the black-outs, the lasers.</summary>
    Draw,
    /// <summary>Firing at a hero in sight.</summary>
    Fire,
    /// <summary>Hunting a hero he no longer sees, or searching where he lost one.</summary>
    Lost,
}

/// <summary>The Thresher as the view reads it each frame: where, which way his beam points, his state and how suspicious he is.</summary>
public readonly record struct StalkerLook(int Id, Vector2 Position, float Heading, StalkerState State, float Suspicion, int Quarry, bool Moving);

/// <summary>
/// How one of the Thresher's plants is heard at the listener (design-stealth §9.1): its gain in dB, how much of the near family
/// (heavy, close: thresher_plant_1..3), the middle one (thresher_mid_1..3) and the far one (thresher_step_1..2), whether the
/// sub thump comes with it and the grate under the listener rattles. Every foot sounds alike (user 2026-11-27).
/// </summary>
public readonly record struct StompMix(float Db, float Near, float Mid, float Far, bool Sub, bool Rattle, int Walls, int Grates);

/// <summary>
/// Section 2's Thresher for the view (package S): which machine he is, what he is doing (<see cref="StalkerState"/>), whether
/// a hero stands in his light, and how his plants carry through the deck to the listener. Read from the world; it changes
/// nothing, so every client and every replay hears and sees the same from the same state. Free of Godot (the tests read it).
/// </summary>
public static class StoryStalker
{
    /// <summary>The word the music reads for a state (<c>thresher state &lt;word&gt;</c>, StoryMusicCue).</summary>
    public static string Word(StalkerState state) => state switch
    {
        StalkerState.Dwell => "dwell",
        StalkerState.Wary => "wary",
        StalkerState.Draw => "draw",
        StalkerState.Fire => "fire",
        StalkerState.Lost => "lost",
        _ => "calm",
    };

    /// <summary>The Thresher under section 2's rule now (the stealth flag on, cast in his role, a searchlight); null otherwise.</summary>
    public static Machine? Of(GameWorld world)
    {
        if (world.Story is not { } run || !run.Flags.Contains(StoryMarks.Stealth))
            return null;
        foreach (var machine in run.Director.Of(world, Prologue.ThresherRole))
        {
            if (!machine.IsDead && world.StoryStalks(machine))
                return machine;
        }
        return null;
    }

    /// <summary>What he is doing now, from his order and his seeker's suspicion (design-stealth §4's table).</summary>
    public static StalkerState StateOf(GameWorld world, Machine thresher)
    {
        if (world.StorySeekerOf(thresher.Id) is not { } eyes)
            return StalkerState.Calm;
        var now = world.Time;
        switch (thresher.Order)
        {
            case AttackOrder { Target: var target }:
                if (eyes.Sees.Contains(target) && eyes.SightSince.TryGetValue(target, out var since))
                    return now - since < eyes.Draw ? StalkerState.Draw : StalkerState.Fire;
                return StalkerState.Lost;
            case SearchOrder:
                // A lure's search has no quarry (GameWorld.StoryLure); one after a lost hunt has.
                return eyes.Quarry != 0 && eyes.SeenAt >= 0 ? StalkerState.Lost : StalkerState.Wary;
        }
        if (eyes.Wary && eyes.Suspicion >= StorySight.WaryAt)
            return StalkerState.Wary;
        var run = world.Story;
        var standing = eyes.DwellUntil > now
            || run is not null && (run.Stealth.Looks.ContainsKey(thresher.Id) || run.Stealth.Guards.ContainsKey(thresher.Id) && thresher.Order is IdleOrder);
        return standing ? StalkerState.Dwell : StalkerState.Calm;
    }

    /// <summary>All of him the view needs this frame, or null without a Thresher under section 2's rule.</summary>
    public static StalkerLook? Look(GameWorld world)
    {
        if (Of(world) is not { } thresher)
            return null;
        var eyes = world.StorySeekerOf(thresher.Id);
        var moving = thresher.Path.Count > thresher.PathIndex && thresher.Order is not IdleOrder;
        return new StalkerLook(thresher.Id, thresher.Position, world.GunHeading(thresher), StateOf(world, thresher),
            eyes?.Suspicion ?? 0, eyes?.Quarry ?? 0, moving);
    }

    /// <summary>
    /// Whether a hero stands in his light: within his beam's length (<see cref="StorySight.BeamLength"/>), inside its cone and
    /// with his 9-m eye clear to it, whether or not he has noticed it yet (the HUD's IM LICHT, design-stealth §9.4).
    /// </summary>
    public static bool InLight(GameWorld world, Machine thresher, Machine hero)
    {
        var way = hero.Position - thresher.Position;
        if (way.Length() > StorySight.BeamLength)
            return false;
        if (way.LengthSquared() > 0.01f)
        {
            var off = MathF.IEEERemainder(MathF.Atan2(way.Y, way.X) - world.GunHeading(thresher), MathF.Tau);
            if (MathF.Abs(off) > StorySight.BeamHalfAngle * MathF.PI / 180)
                return false;
        }
        return world.StoryLampOn(thresher.Id) && world.StoryEyeClear(thresher, hero);
    }

    // ---- his plants through the deck ------------------------------------------------------------------

    // Play test 2026-11-25: on deck A his steps were not heard at all (80-115 m, behind a wall or three: -6 to -14 dB on top of
    // the distance's -17 dB, under the music). He is to be heard wherever he walks, so the player always knows where he is:
    // the walls and the distance take little of his loudness. User 2026-11-27: three families of steps the user picked by ear,
    // near, middle and far, each sounding its distance itself, so no low-pass any more: only the loudness changes.

    /// <summary>Behind a wall or a high stack a plant loses this many dB (and <see cref="WallDbEach"/> each further one, down to <see cref="WallDbLeast"/>); through a grate it loses <see cref="GrateDb"/>.</summary>
    public const float WallDb = -3, WallDbEach = -1.5f, WallDbLeast = -8, GrateDb = -1;

    /// <summary>
    /// The three families by the distance: only the near one (thresher_plant_1..3, heavy and close) within
    /// <see cref="NearWithin"/>, cross-faded into the middle one (thresher_mid_1..3) by <see cref="MidFrom"/>, the middle one alone
    /// to <see cref="MidWithin"/>, cross-faded into the far one (thresher_step_1..2) by <see cref="FarFrom"/>, the far one beyond.
    /// </summary>
    public const float NearWithin = 25, MidFrom = 40, MidWithin = 80, FarFrom = 120;

    /// <summary>How much of the near, the middle and the far family a plant at <paramref name="distance"/> metres has (adding up to 1).</summary>
    public static (float Near, float Mid, float Far) Families(float distance)
    {
        var near = Math.Clamp((MidFrom - distance) / (MidFrom - NearWithin), 0, 1);
        var far = Math.Clamp((distance - MidWithin) / (FarFrom - MidWithin), 0, 1);
        return (near, 1 - near - far, far);
    }

    /// <summary>
    /// A plant's extra loudness by its <paramref name="weight"/>: none for any foot of his walk (up to 1; front and back sound
    /// alike, user 2026-11-27), 6 dB more for each doubling above it (the hunt's gallop).
    /// </summary>
    public static float WeightDb(float weight) => weight <= 1 ? 0 : 6 * MathF.Log2(weight);

    /// <summary>The sub thump comes with a plant within this many metres; a grate under the listener rattles within <see cref="RattleWithin"/>; his breath every second plant within <see cref="BreathWithin"/>.</summary>
    public const float SubWithin = 120, RattleWithin = 30, BreathWithin = 25;

    /// <summary>Beyond this a plant is not played at all (the bus's MaxDistance): most of a deck.</summary>
    public const float Audible = 420;

    /// <summary>The plants' unit size: the distance falls off slowly from it (-7 dB at 100 m, -13 dB at 200 m).</summary>
    public const float StompUnit = 45;

    /// <summary>A plant shakes the view within this many metres, by <see cref="ShakeStrength"/>.</summary>
    public const float ShakeWithin = 30;

    /// <summary>How hard a plant shakes the view at a distance: (1 - d/40)², nothing beyond <see cref="ShakeWithin"/> (design-stealth §9.4).</summary>
    public static float ShakeStrength(float distance) =>
        distance >= ShakeWithin ? 0 : MathF.Pow(1 - distance / 40, 2);

    /// <summary>
    /// The walls (and high stacks) and the grates a straight line crosses on the deck's grid, each run of cells counted once,
    /// sampled every half metre; the cells at the two ends are left out (a foot stands beside its wall).
    /// </summary>
    public static (int Walls, int Grates) Occlusion(Deck deck, Vector2 from, Vector2 to)
    {
        var length = Vector2.Distance(from, to);
        var samples = (int)MathF.Ceiling(length / 0.5f);
        var (walls, grates) = (0, 0);
        var last = DeckCell.Floor;
        for (var i = 1; i < samples; i++)
        {
            var at = Vector2.Lerp(from, to, i / (float)samples);
            if (Vector2.Distance(at, from) < 1 || Vector2.Distance(at, to) < 1)
                continue;
            var cell = deck.Cell((int)MathF.Floor(at.X), (int)MathF.Floor(at.Y)) switch
            {
                DeckCell.Wall or DeckCell.High => DeckCell.Wall,
                DeckCell.Grate => DeckCell.Grate,
                _ => DeckCell.Floor,
            };
            if (cell != last)
            {
                if (cell == DeckCell.Wall)
                    walls++;
                else if (cell == DeckCell.Grate)
                    grates++;
            }
            last = cell;
        }
        return (walls, grates);
    }

    /// <summary>
    /// How a plant at <paramref name="distance"/> metres is heard through <paramref name="walls"/> walls and
    /// <paramref name="grates"/> grates: -3 dB behind a wall (1.5 dB more for each further one, down to -8), -1 dB a grate;
    /// the shares of the three families (<see cref="Families"/>); the sub within 120 m.
    /// </summary>
    public static StompMix Mix(float distance, int walls, int grates)
    {
        var db = walls > 0 ? MathF.Max(WallDbLeast, WallDb + WallDbEach * (walls - 1)) : 0;
        db += GrateDb * grates;
        var (near, mid, far) = Families(distance);
        return new StompMix(db, near, mid, far, distance <= SubWithin, distance <= RattleWithin, walls, grates);
    }

    /// <summary>How suspicious he is of a hero, 0 to 1 (the eye glyph's fill): his own suspicion when that hero is his quarry.</summary>
    public static float SuspicionOf(GameWorld world, StalkerLook look, Machine hero) =>
        look.Quarry == hero.Id || look.State is StalkerState.Wary && look.Quarry == 0 ? look.Suspicion : 0;
}
