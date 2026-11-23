using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.World;
using static Machines.Sim.Story.Prologue;

namespace Machines.Sim.Story;

/// <summary>
/// The armoury's waves on the director's ticks (plan stufe7 §4 W, design-waves §4–6): the pause before each batch with its
/// bar and early call, the batch's spawns behind its doors (at most <see cref="StoryWaves.MostAlive"/> alive), the doors it
/// opens and the barricades it shoots away, the press and the anti-hang review, its end; the boss's escorts and the
/// marked wrecks at the end. <see cref="Then.Armoury"/> looks once a tick (Prologue.Section6's beat <c>armoury</c>).
/// </summary>
internal static class StoryArmoury
{
    /// <summary>A look of the waves: the pause, the batch, the boss's charge, L-7's self-repair.</summary>
    internal static void Look(GameWorld world, StoryRun run, StoryDirector director)
    {
        var waves = run.Waves;
        var now = world.Time;
        waves.Locator = world.HeroOf(run.Locator)?.Id ?? 0;
        foreach (var (id, foe) in waves.Foes.ToList())
        {
            if (world.Find<Machine>(id) is not { IsDead: false } machine)
            {
                waves.Foes.Remove(id);
                continue;
            }
            if (machine.HitPoints < foe.HitPoints)
                foe.HurtAt = now;
            foe.HitPoints = machine.HitPoints;
        }
        if (waves.Fighting)
            Fight(world, run, director, waves, now);
        else if (run.Flags.Contains(StoryMarks.ArmourySealed) && waves.Wave > waves.Cleared && waves.Wave <= StoryWaves.Plan.Count)
            Pause(world, run, director, waves, now);
        world.UpdateCharge(run);
        world.UpdateArmouryRepair(run);
    }

    // ---- the pause ----------------------------------------------------------------------------------

    /// <summary>Starts the pause before batch <paramref name="wave"/>: its bar, its next door named, a Grunt sent to barricade it.</summary>
    internal static void BeginPause(GameWorld world, StoryRun run, StoryDirector director, int wave, float? seconds = null)
    {
        var waves = run.Waves;
        var plan = StoryWaves.Plan[wave - 1];
        waves.Wave = wave;
        waves.Fighting = false;
        waves.PauseStart = world.Time;
        waves.PauseEnds = world.Time + (seconds ?? plan.PauseBefore);
        waves.BarFrom = wave == 1 ? StoryWaves.FirstBarFrom : 0;
        waves.Called = false;
        StoryEvents.Cue(world, "wave pause");
        // The computer's Ike blocks the next door it can (R or T) with crates; Bob stays by the Locator to be mended and armed.
        if (wave < StoryWaves.Plan.Count && StoryWaves.DoorsOf(wave).FirstOrDefault(d => d is 'R' or 'T') is var door && door != default)
            new Then.Assign(1, CompanionTask.Barricade, door.ToString(), IfFree: true).Do(world, run, director);
        if (wave == 2)
            StoryEvents.Cue(world, "hint batch");
    }

    private static void Pause(GameWorld world, StoryRun run, StoryDirector director, StoryWaves waves, float now)
    {
        // The early call: everyone ready, the bar jumps to its last seconds (the first pause, the lockdown's, is short anyway).
        if (!waves.Called && waves.Wave > 1 && waves.PauseEnds - now > StoryWaves.EarlyCall && CrewReady(world, run, StoryWaves.ReadyShare))
        {
            waves.Called = true;
            var share = waves.Bar(now);
            waves.PauseEnds = now + StoryWaves.EarlyCall;
            // The bar goes on from where it stood to full in the last seconds.
            waves.PauseStart = now;
            waves.BarFrom = share;
            StoryEvents.Cue(world, $"wave early {waves.Wave}");
        }
        if (now >= waves.PauseEnds)
            BeginWave(world, run, director, waves.Wave);
    }

    /// <summary>
    /// The trio stands ready: both Grunts up at <paramref name="share"/> of their hit points or more, the Locator too, and no
    /// Bolter carried that an unarmed Grunt could take.
    /// </summary>
    internal static bool CrewReady(GameWorld world, StoryRun run, float share)
    {
        for (var i = 0; i < run.Seats.Count; i++)
        {
            if (world.HeroOf(run.Seats[i]) is not { } hero || i > 0 && (world.CompanionDown(run.Seats[i]) || world.CompanionDormant(run.Seats[i])))
                return false;
            if (hero.HitPoints < hero.MaxHitPoints * share)
                return false;
            if (i > 0 && run.Waves.Carried is not null && hero.Weapons.Count == 0)
                return false;
        }
        return true;
    }

    // ---- the batch ----------------------------------------------------------------------------------

    private static void BeginWave(GameWorld world, StoryRun run, StoryDirector director, int wave)
    {
        var waves = run.Waves;
        var now = world.Time;
        waves.Wave = wave;
        waves.Fighting = true;
        waves.WaveStart = now;
        waves.Spawned = 0;
        waves.Due.Clear();
        waves.Due.AddRange(StoryWaves.Plan[wave - 1].Units);
        // Co-op: each person in a Grunt's seat brings one more Bolter Grunt by T in batches 2–4.
        if (wave is >= 2 and <= 4)
        {
            foreach (var _ in run.Seats.Skip(1).Where(s => !run.Bots.Contains(s)))
                waves.Due.Add(new StoryWaveUnit(StoryWaveKind.Bolter, 'T', "wave_t_3", StoryWaves.CoopDelay));
        }
        waves.Opened.Clear();
        waves.Barricades.Clear();
        waves.NextPress = now + StoryWaves.PressEvery;
        waves.NextReview = now + StoryWaves.ReviewEvery;
        // The Grunts drop their crates and fight (CompanionBot.Armoury).
        for (var seat = 1; seat < run.Seats.Count; seat++)
        {
            if (run.Crew.Of(run.Seats[seat]).Task == CompanionTask.Barricade)
                new Then.Assign(seat, CompanionTask.None).Do(world, run, director);
        }
        new Then.Say(wave switch
        {
            1 => Lines.AutoWave1,
            2 => Lines.AutoWave2,
            3 => Lines.AutoWave3,
            4 => Lines.AutoWave4,
            _ => Lines.AutoWave5,
        }).Do(world, run, director);
        StoryEvents.Cue(world, $"wave {wave} alive");
        if (wave == StoryWaves.Plan.Count)
        {
            // The heavy one bursts gate N: the leaves fly into the room and the gate is gone for good.
            StoryEvents.Cue(world, "gate N burst", StoryAction.Where(world, "boss_n"));
            new Then.BlowDoor('N').Do(world, run, director);
            waves.Opened.Add('N');
        }
        Spawn(world, run, director, waves, now);
    }

    private static void Fight(GameWorld world, StoryRun run, StoryDirector director, StoryWaves waves, float now)
    {
        Spawn(world, run, director, waves, now);
        foreach (var (door, until) in waves.Barricades.ToList())
        {
            if (now < until)
                continue;
            // The crates are shot away and thrown into the room; the units by that door go on to their posts.
            waves.Barricades.Remove(door);
            new Then.ClearBarricade(door.ToString()).Do(world, run, director);
            foreach (var (id, foe) in waves.Foes.Where(f => f.Value.Door == door))
                if (world.Find<Machine>(id) is { IsDead: false } machine)
                    Send(world, run, machine, foe);
        }
        if (now >= waves.NextPress)
        {
            waves.NextPress = now + StoryWaves.PressEvery;
            Press(world, run, waves);
        }
        if (now >= waves.NextReview)
        {
            waves.NextReview = now + StoryWaves.ReviewEvery;
            Review(world, run, waves, now);
        }
        if (waves.Due.Count == 0 && waves.Alive(world) == 0)
            EndWave(world, run, director, waves);
    }

    /// <summary>The units whose delay has come, while fewer than <see cref="StoryWaves.MostAlive"/> of the batch stand.</summary>
    private static void Spawn(GameWorld world, StoryRun run, StoryDirector director, StoryWaves waves, float now)
    {
        while (waves.Due.Count > 0)
        {
            var next = waves.Due.OrderBy(u => u.Delay).First();
            if (now - waves.WaveStart < next.Delay || waves.Alive(world) >= StoryWaves.MostAlive)
                return;
            waves.Due.Remove(next);
            SpawnUnit(world, run, director, waves, next, now);
        }
    }

    private static void SpawnUnit(GameWorld world, StoryRun run, StoryDirector director, StoryWaves waves, StoryWaveUnit unit, float now)
    {
        OpenDoor(world, run, director, waves, unit.Door);
        var role = StoryWaves.RoleOf(unit.Kind);
        var before = director.Cast.TryGetValue(role, out var cast) ? cast.Count : 0;
        if (unit.Kind == StoryWaveKind.Heavy)
        {
            // Old Iron: 100 hit points and 20 for every Grunt that carries a Bolter as it comes (most 140), 15 % more for each person in a Grunt's seat.
            var bolters = run.Seats.Skip(1).Count(s => world.HeroOf(s) is { Weapons.Count: > 0 } && !world.CompanionDown(s));
            new Then.SpawnHeavy(role, StoryHeavyPart.Boss, [unit.Staging], Bolters: bolters).Do(world, run, director);
        }
        else
            new Then.Spawn(role, StoryWaves.UnitOf(unit.Kind), [unit.Staging], DefCon.Low, Looks: Race.Red).Do(world, run, director);
        new Then.Pace(role, StoryWaves.PaceOf(unit.Kind)).Do(world, run, director);
        foreach (var id in director.Cast[role].Skip(before))
        {
            if (world.Find<Machine>(id) is not { } machine)
                continue;
            if (unit.Kind == StoryWaveKind.Heavy)
            {
                var people = run.Seats.Skip(1).Count(s => !run.Bots.Contains(s));
                if (people > 0)
                    world.TuneForStory(machine, (int)MathF.Round(machine.MaxHitPoints * (1 + StoryWaves.CoopBossShare * people)));
                waves.Boss = id;
                waves.Phase = StoryChargePhase.Cruise;
                waves.PhaseSince = now;
                waves.NextCharge = now + GameWorld.ChargeEvery;
            }
            var foe = new StoryWaveFoe(waves.Wave, unit.Kind, unit.Door, unit.Staging)
            {
                HitPoints = machine.HitPoints,
                HurtAt = now,
                StillAt = machine.Position,
                StillSince = now,
            };
            waves.Foes[id] = foe;
            waves.Spawned++;
            if (waves.Barricades.ContainsKey(unit.Door) && world.BarricadeCrates(unit.Door.ToString()) is { Count: > 0 } crates)
            {
                foreach (var crate in crates)
                    director.Floors[crate.Id] = Math.Max(1, crate.MaxHitPoints / 6);
                var crateAt = crates[waves.Spawned % crates.Count];
                world.SendForStory(machine, new AttackOrder(crateAt.Id), crateAt.Position);
            }
            else
                Send(world, run, machine, foe);
            if (unit.Kind is StoryWaveKind.Zap or StoryWaveKind.TankZap)
                run.Flags.Add(ZapSeen);
        }
    }

    /// <summary>Flag: the first electric unit came (the beat <c>zap</c> says it).</summary>
    internal const string ZapSeen = "armoury_zap";

    /// <summary>A door a unit comes by opens, once a batch: Bob calls it; with two crates or more in its slots its units shoot them away first.</summary>
    private static void OpenDoor(GameWorld world, StoryRun run, StoryDirector director, StoryWaves waves, char door)
    {
        if (!waves.Opened.Add(door))
            return;
        if (door is 'R' or 'T' && world.BarricadeCrates(door.ToString()).Count >= StoryWaves.BarricadeCrates)
        {
            waves.Barricades[door] = world.Time + (waves.Wave <= 2 ? StoryWaves.BarricadeEarly : StoryWaves.BarricadeLate);
            StoryEvents.Cue(world, $"barricade hit {door}");
        }
        if (!director.Blown.Contains(door))
        {
            if (door == 'N')
                StoryEvents.Cue(world, "gate N open", StoryAction.Where(world, "wave_n_1"));
            new Then.OpenDoors(door.ToString()).Do(world, run, director);
        }
        new Then.SayToSeat(door switch { 'R' => Lines.BobDoorR, 'T' => Lines.BobDoorT, _ => Lines.BobDoorN }, 2).Do(world, run, director);
    }

    /// <summary>A unit to its work: a Bolter or a Grunt 2 to its post by its door, an electric one and the boss at the Locator.</summary>
    internal static void Send(GameWorld world, StoryRun run, Machine machine, StoryWaveFoe foe)
    {
        if (foe.Kind is StoryWaveKind.Zap or StoryWaveKind.TankZap or StoryWaveKind.Heavy)
        {
            if (Quarry(world, run, machine.Position) is { } hero)
                world.SendForStory(machine, new AttackOrder(hero.Id), hero.Position);
            return;
        }
        var posts = StoryWaves.PostsOf(foe.Door);
        var index = foe.Staging.Length > 0 && char.IsDigit(foe.Staging[^1]) ? Math.Clamp(foe.Staging[^1] - '1', 0, posts.Count - 1) : 0;
        var at = StoryAction.Where(world, posts[index]);
        world.SendForStory(machine, new MoveOrder(at), at);
    }

    /// <summary>The hero of the trio a unit goes for: the Locator while it stands, else the nearest Grunt standing; null when none.</summary>
    internal static Machine? Quarry(GameWorld world, StoryRun run, Vector2 from)
    {
        Machine? best = null;
        foreach (var seat in run.Seats)
        {
            if (world.HeroOf(seat) is not { IsDead: false } hero || world.StoryInert(hero))
                continue;
            if (seat == run.Locator)
                return hero;
            if (best is null || Vector2.DistanceSquared(hero.Position, from) < Vector2.DistanceSquared(best.Position, from))
                best = hero;
        }
        return best;
    }

    /// <summary>The press: the unit of the batch hurt longest ago (not a rusher, not the boss) goes for the Locator, so camping one corner fails slowly.</summary>
    private static void Press(GameWorld world, StoryRun run, StoryWaves waves)
    {
        var pressed = waves.Foes
            .Where(f => f.Value.Kind is StoryWaveKind.Bolter or StoryWaveKind.Tank && world.Find<Machine>(f.Key) is { IsDead: false } m
                && !(m.Order is AttackOrder attack && world.Find<Machine>(attack.Target) is { } aim && world.InTrioForStory(aim)))
            .OrderBy(f => f.Value.HurtAt).ThenBy(f => f.Key).Select(f => world.Find<Machine>(f.Key)!).FirstOrDefault();
        if (pressed is null || Quarry(world, run, pressed.Position) is not { } hero)
            return;
        world.SendForStory(pressed, new AttackOrder(hero.Id), hero.Position);
        waves.Presses++;
    }

    /// <summary>
    /// The anti-hang review (from Wired's ReviewFoes): an idle unit gets its order again; one that has not moved
    /// <see cref="StoryWaves.HangMetres"/> in <see cref="StoryWaves.HangSeconds"/>, away from its post and seeing nobody, is set
    /// back behind its door, out of sight, and sent again.
    /// </summary>
    private static void Review(GameWorld world, StoryRun run, StoryWaves waves, float now)
    {
        foreach (var (id, foe) in waves.Foes)
        {
            if (world.Find<Machine>(id) is not { IsDead: false } machine || id == waves.Boss && waves.Phase != StoryChargePhase.Cruise
                || world.HeldByStory(id) || waves.Barricades.ContainsKey(foe.Door))
                continue;
            if (Vector2.Distance(machine.Position, foe.StillAt) >= StoryWaves.HangMetres)
                (foe.StillAt, foe.StillSince) = (machine.Position, now);
            if (machine.Order is IdleOrder or StandGroundOrder && world.StorySeekerOf(id) is not { Sees.Count: > 0 })
            {
                Send(world, run, machine, foe);
                continue;
            }
            var post = foe.Kind is StoryWaveKind.Bolter or StoryWaveKind.Tank
                ? StoryAction.Where(world, StoryWaves.PostsOf(foe.Door)[Math.Clamp(foe.Staging[^1] - '1', 0, 2)]) : (Vector2?)null;
            if (now - foe.StillSince < StoryWaves.HangSeconds || post is { } p && Vector2.Distance(machine.Position, p) <= 3
                || world.StorySeekerOf(id) is { Sees.Count: > 0 } || foe.Kind == StoryWaveKind.Heavy)
                continue;
            world.SetBackForStory(machine, StoryAction.Where(world, foe.Staging));
            (foe.StillAt, foe.StillSince) = (machine.Position, now);
            Send(world, run, machine, foe);
            waves.Fetched++;
        }
    }

    private static void EndWave(GameWorld world, StoryRun run, StoryDirector director, StoryWaves waves)
    {
        var wave = waves.Wave;
        waves.Fighting = false;
        waves.Cleared = wave;
        waves.Foes.Clear();
        waves.Due.Clear();
        // The doors it opened shut again (N only after batch 3 and 4; the boss blew it).
        var shut = new string(waves.Opened.Where(d => !director.Blown.Contains(d)).ToArray());
        if (shut.Length > 0 && wave < StoryWaves.Plan.Count)
            new Then.CloseDoors(shut).Do(world, run, director);
        waves.Opened.Clear();
        waves.Barricades.Clear();
        StoryEvents.Cue(world, $"wave {wave} clear");
        if (wave < StoryWaves.Plan.Count)
        {
            new Then.SayToSeat(Lines.BobClearShort, 2).Do(world, run, director);
            if (wave == 2)
                new Then.SayToSeat(Lines.IkeClear, 1).Do(world, run, director);
            if (wave == 2)
                new Then.Checkpoint(9).Do(world, run, director);
            if (wave == 4)
                new Then.Checkpoint(10).Do(world, run, director);
            BeginPause(world, run, director, wave + 1);
        }
    }

    // ---- the end ------------------------------------------------------------------------------------

    /// <summary>
    /// Marks the wrecks to strip for the stash: the last electric unit to die (the electric charge) and the last Grunt 2 (its
    /// drive), a Grunt 1 electric before a Grunt 2 one and a Grunt 2 with a Bolter before one with a charge, so both marks
    /// fall on two wrecks where the batches left them.
    /// </summary>
    internal static void MarkWrecks(StoryWaves waves)
    {
        StoryWreck? Last(params StoryWaveKind[] kinds)
        {
            foreach (var kind in kinds)
            {
                if (waves.Wrecks.Where(w => w.Value.Kind == kind && w.Value.Marked is null).Select(w => w.Value).LastOrDefault() is { } wreck)
                    return wreck;
            }
            return null;
        }
        if (Last(StoryWaveKind.Zap, StoryWaveKind.TankZap) is { } zap)
            zap.Marked = StoryWaves.ElectricCharge;
        if (Last(StoryWaveKind.Tank, StoryWaveKind.TankZap) is { } tank)
            tank.Marked = StoryWaves.Grunt2Drive;
    }
}

public static partial class Then
{
    /// <summary>A look of the armoury's waves, every tick of section 6 (the beat <c>armoury</c>; StoryArmoury).</summary>
    public sealed record Armoury : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director) => StoryArmoury.Look(world, run, director);
    }

    /// <summary>The pause before batch <paramref name="Wave"/> begins (its bar, the door named); <paramref name="Seconds"/> overrides its length.</summary>
    public sealed record Batch(int Wave, float? Seconds = null) : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director) => StoryArmoury.BeginPause(world, run, director, Wave, Seconds);
    }

    /// <summary>The boss's two escorts join the batch (at its half).</summary>
    public sealed record Escort : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            foreach (var unit in StoryWaves.Escorts)
                run.Waves.Due.Add(unit with { Delay = world.Time - run.Waves.WaveStart + unit.Delay });
            // The doors open again for them (once each).
            run.Waves.Opened.Remove('R');
            run.Waves.Opened.Remove('T');
        }
    }

    /// <summary>The wrecks to strip for the stash are marked (StoryArmoury.MarkWrecks).</summary>
    public sealed record MarkWrecks : StoryAction
    {
        internal override void Do(GameWorld world, StoryRun run, StoryDirector director)
        {
            StoryArmoury.MarkWrecks(run.Waves);
            foreach (var (id, wreck) in run.Waves.Wrecks.Where(w => w.Value.Marked is not null))
                StoryEvents.Cue(world, $"wreck marked {id} {wreck.Marked}", wreck.Position);
        }
    }
}

public static partial class When
{
    /// <summary>Batch <paramref name="Wave"/> is in the room (or was).</summary>
    public sealed record WaveOn(int Wave) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            run.Waves.Cleared >= Wave || run.Waves.Fighting && run.Waves.Wave == Wave;
    }

    /// <summary>Batch <paramref name="Wave"/> is cleared.</summary>
    public sealed record WaveClear(int Wave) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) => run.Waves.Cleared >= Wave;
    }

    /// <summary>The trio stands ready (StoryArmoury.CrewReady): everyone at <paramref name="Share"/> of their hit points, no Bolter left to mount.</summary>
    public sealed record CrewReady(float Share) : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) => StoryArmoury.CrewReady(world, run, Share);
    }

    /// <summary>Both end strips are done (the salvage set has both parts).</summary>
    public sealed record Salvaged : StoryTrigger
    {
        internal override bool Holds(GameWorld world, StoryRun run, StoryDirector director) =>
            run.Salvage.Contains(StoryWaves.Grunt2Drive) && run.Salvage.Contains(StoryWaves.ElectricCharge);
    }
}
