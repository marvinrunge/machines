using System.Globalization;
using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Tests.Story;

/// <summary>
/// The armoury played by a modelled player (stage 7, package W): the Locator's fire as steady hit points a second on the
/// nearest foe, and in the pauses what a player does at the workbench (put a Grunt together, strip a Bolter, mount it, mend),
/// each by its real hold. Notes each batch's start and clear, each pause's work and the boss's charges.
/// </summary>
internal sealed class ArmouryPlay
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    public GameWorld World { get; }
    public StoryRun Run => World.Story!;
    public StoryWaves Waves => Run.Waves;

    /// <summary>The Locator's fire: hit points a second on the nearest foe within 40 m (0: it does not fire).</summary>
    public float Fire { get; set; } = 8;

    /// <summary>Whether the Locator works at the workbench (and puts Grunts together).</summary>
    public bool Works { get; set; } = true;

    public List<string> Log { get; } = [];
    public SortedDictionary<int, (float Start, float End)> Batches { get; } = [];
    public int Revived { get; private set; }
    public float? BossIn { get; private set; }
    public float? BossDown { get; private set; }

    private int _wave;
    private bool _fighting;
    private string? _holding;
    private (int Revived, int Mended, int Stripped, int Mounted) _pause;

    /// <summary>What was done so far: Grunts put together, mends, strips, mounts.</summary>
    private (int Revived, int Mended, int Stripped, int Mounted) Work => (Revived, Waves.Mends, Waves.Stripped, Waves.Mounted.Count);

    public ArmouryPlay(int checkpoint, int seed = 3)
    {
        World = MatchSetup.Create(DataLoader.Value, StoryOptions.Prologue, StoryOptions.Prologue,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Story = new StoryOptions { Checkpoint = checkpoint }, FogOfWar = false });
        (_wave, _fighting) = (Waves.Wave, Waves.Fighting);
    }

    public Machine Hero(int seat) => World.HeroOf(Run.Seats[seat])!;

    /// <summary>From checkpoint 8: the trio in the armoury, the Locator takes the Bolter from its rack, and the room bolts shut.</summary>
    public void Seal()
    {
        var rack = World.Deck!.Marker("pickup_bolter").Position;
        SetDown(Hero(0), rack + new Vector2(-1.5f, -1.5f));
        SetDown(Hero(1), rack + new Vector2(-6, -3));
        SetDown(Hero(2), rack + new Vector2(-5, 3));
        World.Step();
        World.Issue(new StoryUseCommand(Race.Red, "pickup_bolter"));
        if (!Until(() => Run.Director.Has("lockdown"), 30, play: false))
            throw new InvalidOperationException("the armoury did not seal");
    }

    /// <summary>Steps (playing, unless told not to) until done or the seconds are up; whether it was done.</summary>
    public bool Until(Func<bool> done, float seconds, bool play = true, Action? each = null)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
        {
            if (done() || Run.Over)
                return done();
            if (play)
                Play();
            Step();
            each?.Invoke();
        }
        return done();
    }

    /// <summary>One tick, its batch changes noted.</summary>
    public void Step()
    {
        World.Step();
        if (Waves.Fighting && !_fighting)
        {
            Batches[Waves.Wave] = (World.Time, float.NaN);
            var work = Work;
            if (Waves.Wave > 1)
                Log.Add($"pause {Waves.Wave - 1}: revived {work.Revived - _pause.Revived} mended {work.Mended - _pause.Mended} stripped {work.Stripped - _pause.Stripped} mounted {work.Mounted - _pause.Mounted}");
            Log.Add(string.Create(CultureInfo.InvariantCulture, $"wave {Waves.Wave} start tick {World.Tick} at {World.Time:0.0} s"));
        }
        if (!Waves.Fighting && _fighting && Batches.TryGetValue(_wave, out var batch))
        {
            Batches[_wave] = (batch.Start, World.Time);
            Log.Add(string.Create(CultureInfo.InvariantCulture, $"wave {_wave} clear tick {World.Tick} seconds {World.Time - batch.Start:0.0}"));
            _pause = Work;
        }
        if (Waves.Boss != 0 && BossIn is null)
            BossIn = World.Time;
        if (BossIn is not null && BossDown is null && World.Find<Machine>(Waves.Boss) is not { IsDead: false })
        {
            BossDown = World.Time;
            Log.Add(string.Create(CultureInfo.InvariantCulture, $"boss charges {Waves.Charges} hits {Waves.ChargeHits} crashes {Waves.Crashes} fight {BossDown - BossIn:0.0} s"));
        }
        (_wave, _fighting) = (Waves.Wave, Waves.Fighting);
    }

    /// <summary>What the modelled player does on this tick.</summary>
    public void Play()
    {
        if (World.HeroOf(Run.Locator) is not { IsDead: false } locator || World.LocatorShutDown)
            return;
        if (Fire > 0 && World.Tick % (GameWorld.TicksPerSecond / 4) == 0)
        {
            var foe = Waves.Foes.Keys.Select(id => World.Find<Machine>(id)).OfType<Machine>().Where(m => !m.IsDead)
                .Where(m => Vector2.Distance(m.Position, locator.Position) <= 40)
                .OrderBy(m => Vector2.Distance(m.Position, locator.Position)).ThenBy(m => m.Id).FirstOrDefault();
            if (foe is not null)
                World.Damage(foe, Math.Max(1, (int)MathF.Round(Fire / 4)), Race.Red, locator, "BOLTER");
        }
        // A hold under way: until it ends.
        if (_holding is not null)
        {
            if (Run.Crew.Holding.ContainsKey(Run.Locator.Value))
                return;
            if (_holding.StartsWith(GameWorld.SeatTarget, StringComparison.Ordinal) && !Run.Seats.Skip(1).Any(World.CompanionDown))
                Revived++;
            _holding = null;
        }
        if (!Works)
            return;
        var down = Run.Seats.Skip(1).FirstOrDefault(s => World.CompanionDown(s));
        if (down.Value != 0)
        {
            Hold(GameWorld.SeatTarget + down.Value, World.HeroOf(down)!.Position);
            return;
        }
        if (Waves.Fighting && Run.Flags.Contains(StoryMarks.ArmourySealed))
            return;
        var unarmed = Run.Seats.Skip(1).FirstOrDefault(s => World.HeroOf(s) is { Weapons.Count: 0 });
        if (Waves.Carried is not null && unarmed.Value != 0)
        {
            Hold(GameWorld.MountTarget + unarmed.Value, World.HeroOf(unarmed)!.Position);
            return;
        }
        var wreck = Waves.Wrecks.Where(w => GameWorld.StripYield(Run, w.Value) is { } yields && (yields != StoryWaves.Bolter || unarmed.Value != 0))
            .Select(w => (Id: w.Key, w.Value.Position)).FirstOrDefault();
        if (wreck.Id != 0)
        {
            Hold(StoryMarks.WreckTarget + wreck.Id.ToString(CultureInfo.InvariantCulture), wreck.Position);
            return;
        }
        var hurt = Run.Seats.Skip(1).FirstOrDefault(s => World.HeroOf(s) is { } g && g.HitPoints < g.MaxHitPoints * 0.9f);
        if (hurt.Value != 0 && Run.Flags.Contains(StoryMarks.ArmourySealed))
            Hold(GameWorld.MendTarget + hurt.Value, World.HeroOf(hurt)!.Position);
    }

    private void Hold(string target, Vector2 at)
    {
        var locator = Hero(0);
        SetDown(locator, at + Vector2.Normalize(locator.Position - at + new Vector2(0.01f, 0)) * 2.5f);
        World.Issue(new StoryUseCommand(Race.Red, target));
        _holding = target;
    }

    /// <summary>A hero set down on free ground at or near a place.</summary>
    public void SetDown(Machine hero, Vector2 at)
    {
        var mobility = GameWorld.MobilityOf(hero);
        for (var r = 0f; r <= 6; r += 0.5f)
        {
            for (var a = 0; a < 16; a++)
            {
                var p = at + r * new Vector2(MathF.Cos(a * MathF.PI / 8), MathF.Sin(a * MathF.PI / 8));
                if (World.Nav.IsFree(p, mobility))
                {
                    hero.Position = p;
                    return;
                }
            }
        }
        hero.Position = at;
    }
}
