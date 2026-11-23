using System.Globalization;
using Godot;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.Story;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// Package W's view each tick of the armoury (plan stufe7 §4 W, design-waves §8.2), where its cues alone do not say enough:
//   - held sounds (StorySounds.Armoury): the workbench's strip, mount and mend while the Locator holds the use key, the heavy
//     Grunt's motor while it stands and the scrape of its charge while it runs, the barracks lift humming behind gate N from
//     70 % of the bar before batch 3, an electric unit's crackle while it closes on the trio;
//   - the batch bar's soft tick at every tenth of a pause, and a rattle where a unit of the batch leaves its post to press
//     in on the Locator (StoryDirector.Armoury's press);
//   - the wrecks of the batches' units: each lies with its gun on the heap until L-7 strips it, then the gun is gone in a
//     few sparks (a wreck laid down stripped by a checkpoint's set-up lies bare from the start).
// The lights' dip at the lockdown and the power's end are the cues' (Game.StoryArmoury, DeckView.Armoury).
public partial class Game
{
    /// <summary>The keys of the armoury's held sounds start so (StorySounds.Keep).</summary>
    private const string ArmouryHeld = "armoury:";

    /// <summary>An electric unit nearer the trio than this crackles (as its view flickers, ActorView.Armoury).</summary>
    private const float CrackleReach = 16;

    // The world last followed (a checkpoint's restart brings a new one), and what was seen of it.
    private GameWorld? _armouryFollowed;
    private readonly Dictionary<int, int> _holdTicks = [];
    private HashSet<int> _pressing = [];
    private int _armouryPresses;
    private int _barTenth = -1;

    // The guns lying on the wrecks' heaps by wreck (debris id); null once stripped, or for a wreck with nothing to show.
    private readonly Dictionary<int, Node3D?> _wreckGuns = [];

    /// <summary>After every tick of a story (ShowStoryEvents): the armoury's held sounds, ticks, presses and wrecks.</summary>
    private void FollowArmoury()
    {
        var sounds = StoryAudio.Current?.Sounds;
        if (World.Story is not { Section: 6 } run)
        {
            sounds?.Keep(ArmouryHeld, new Dictionary<string, StorySounds.Held>());
            _armouryFollowed = null;
            return;
        }
        var waves = run.Waves;
        if (!ReferenceEquals(_armouryFollowed, World))
        {
            _armouryFollowed = World;
            _holdTicks.Clear();
            _pressing = Pressing(run);
            _armouryPresses = waves.Presses;
            _barTenth = -1;
            _wreckGuns.Clear();
        }
        if (sounds is not null)
            sounds.Keep(ArmouryHeld, ArmouryHeldSounds(run));
        BarTick(run);
        PressRattle(run);
        ShowWreckGuns(run);
        ArmouryTestIfAsked(run);
    }

    /// <summary>The held sounds the armoury wants at this tick, by key.</summary>
    private Dictionary<string, StorySounds.Held> ArmouryHeldSounds(StoryRun run)
    {
        var waves = run.Waves;
        var wanted = new Dictionary<string, StorySounds.Held>(StringComparer.Ordinal);
        // The workbench: a hold that went on this tick sounds where its work is (the wreck, the Grunt).
        foreach (var (player, hold) in waves.Holds)
        {
            var going = hold.Ticks > _holdTicks.GetValueOrDefault(player, 0);
            _holdTicks[player] = hold.Ticks;
            if (!going || HoldSound(run, hold.Target) is not { } sound)
                continue;
            wanted[$"{ArmouryHeld}hold{player}"] = sound;
        }
        foreach (var player in _holdTicks.Keys.Where(p => !waves.Holds.ContainsKey(p)).ToList())
            _holdTicks.Remove(player);
        // Old Iron: its motor while it stands, the wheels' scrape down the locked line while it charges.
        if (waves.Boss != 0 && World.Find<Machine>(waves.Boss) is { IsDead: false } boss)
        {
            var at = ActorView.World(boss.Position) + Vector3.Up * 2;
            wanted[$"{ArmouryHeld}boss"] = new("boss_idle", at, waves.Phase == StoryChargePhase.Stunned ? -8 : -3,
                waves.Phase == StoryChargePhase.Telegraph ? 1.2f : 1);
            if (waves.Phase == StoryChargePhase.Run)
                wanted[$"{ArmouryHeld}run"] = new("container_scrape", at, 2, 0.8f);
        }
        // The barracks lift coming down behind gate N while the bar before batch 3 runs out.
        if (run.Flags.Contains(StoryMarks.ArmourySealed) && !waves.Fighting && waves.Wave == 3 && waves.Bar(World.Time) >= 0.7f
            && World.Deck?.Markers.FirstOrDefault(m => m.Name == "wave_n_1") is { } lift)
            wanted[$"{ArmouryHeld}lift"] = new("lift_hum", ActorView.World(lift.Position) + Vector3.Up * 3, -4);
        // An electric unit closing on the trio crackles.
        foreach (var (id, foe) in waves.Foes)
        {
            if (foe.Kind is not (StoryWaveKind.Zap or StoryWaveKind.TankZap) || World.Find<Machine>(id) is not { IsDead: false } zap
                || !run.Seats.Any(s => World.HeroOf(s) is { } hero && NVector2.Distance(hero.Position, zap.Position) < CrackleReach))
                continue;
            wanted[$"{ArmouryHeld}zap{id}"] = new("electric_crackle", ActorView.World(zap.Position) + Vector3.Up * 1.5f, -6);
        }
        return wanted;
    }

    /// <summary>What a hold of the workbench sounds like and where: strip, mount, mend; null for a target that is gone.</summary>
    private StorySounds.Held? HoldSound(StoryRun run, string target)
    {
        static int? Number(string digits) =>
            int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        if (target.StartsWith(StoryMarks.WreckTarget, StringComparison.Ordinal))
            return Number(target[StoryMarks.WreckTarget.Length..]) is { } id && run.Waves.Wrecks.TryGetValue(id, out var wreck)
                ? new("strip_work", ActorView.World(wreck.Position) + Vector3.Up, -2) : null;
        var (prefix, sound, db) = target.StartsWith(GameWorld.MountTarget, StringComparison.Ordinal)
            ? (GameWorld.MountTarget, "grunt_reassemble", -2f)
            : (GameWorld.MendTarget, "revive_weld", -5f);
        if (!target.StartsWith(prefix, StringComparison.Ordinal) || Number(target[prefix.Length..]) is not { } seat
            || World.HeroOf(new PlayerId(seat)) is not { } grunt)
            return null;
        return new(sound, ActorView.World(grunt.Position) + Vector3.Up * 1.5f, db);
    }

    /// <summary>The batch bar ticks softly at each tenth while a pause runs (design-waves §8.2: the lift's hum and the tick carry the silence).</summary>
    private void BarTick(StoryRun run)
    {
        var waves = run.Waves;
        if (waves.Fighting || waves.Wave < 1 || waves.Wave > StoryWaves.Plan.Count || !run.Flags.Contains(StoryMarks.ArmourySealed))
        {
            _barTenth = -1;
            return;
        }
        var tenth = Mathf.FloorToInt(waves.Bar(World.Time) * 10);
        if (_barTenth >= 0 && tenth > _barTenth && tenth < 10)
            StoryAudio.Play("bar_tick");
        _barTenth = tenth;
    }

    /// <summary>The units of the batch (Bolter Grunts and tanks) that are going for one of the trio now.</summary>
    private HashSet<int> Pressing(StoryRun run)
    {
        var trio = run.Seats.Select(s => World.HeroOf(s)?.Id ?? 0).Where(id => id != 0).ToHashSet();
        return [.. run.Waves.Foes.Where(f => f.Value.Kind is StoryWaveKind.Bolter or StoryWaveKind.Tank
            && World.Find<Machine>(f.Key) is { IsDead: false, Order: AttackOrder attack } && trio.Contains(attack.Target)).Select(f => f.Key)];
    }

    /// <summary>A unit pressed in on the Locator (the press of the batch): its tracks rattle the floor as it leaves its post.</summary>
    private void PressRattle(StoryRun run)
    {
        var now = Pressing(run);
        if (run.Waves.Presses > _armouryPresses)
            foreach (var id in now.Where(id => !_pressing.Contains(id)))
                if (World.Find<Machine>(id) is { } unit)
                    StoryAudio.Play("grating_rattle", ActorView.World(unit.Position));
        _armouryPresses = run.Waves.Presses;
        _pressing = now;
    }

    /// <summary>Each wreck of a batch's unit lies with its gun on the heap; once stripped, the gun is gone in a few sparks.</summary>
    private void ShowWreckGuns(StoryRun run)
    {
        foreach (var (id, wreck) in run.Waves.Wrecks)
        {
            if (!_debris.TryGetValue(id, out var heap) || !IsInstanceValid(heap.View))
                continue;
            if (_wreckGuns.TryGetValue(id, out var gun))
            {
                if (wreck.Stripped && gun is not null)
                {
                    StripWreckGun(gun);
                    _wreckGuns[id] = null;
                }
                continue;
            }
            gun = wreck.Stripped ? null : WreckGun(wreck.Kind, World.FindDebris(id)?.Race ?? run.Options.Ship, wreck.Position, id, heap.View);
            if (gun is not null)
                heap.View.AddChild(gun);
            _wreckGuns[id] = gun;
        }
        foreach (var id in _wreckGuns.Keys.Where(id => !run.Waves.Wrecks.ContainsKey(id)).ToList())
            _wreckGuns.Remove(id);
    }

    /// <summary>The exported machine whose gun lies on a wreck of each kind, and the side it is mounted on.</summary>
    private static (string Model, Mounting Side)? WreckGunOf(StoryWaveKind kind) => kind switch
    {
        StoryWaveKind.Bolter => ("AGGRESSOR_1_GRUNT_R_BOLTER", Mounting.Right),
        StoryWaveKind.Zap => ("AGGRESSOR_1_GRUNT_R_ELECTRIC_CHARGE", Mounting.Right),
        StoryWaveKind.Tank => ("AGGRESSOR_2_GRUNT_L_BOLTER", Mounting.Left),
        StoryWaveKind.TankZap => ("AGGRESSOR_2_GRUNT_L_ELECTRIC_CHARGE", Mounting.Left),
        _ => null,
    };

    /// <summary>
    /// The gun of a wreck's machine, lying on its side on the heap: the parts of the export's rig that its mount carries,
    /// placed as they sit on the mount; null when the export or its mount is missing (the heap then lies as it is).
    /// </summary>
    private Node3D? WreckGun(StoryWaveKind kind, Race race, NVector2 position, int id, Node3D heap)
    {
        if (WreckGunOf(kind) is not { } gunOf || _assets.RigFor(gunOf.Model, Look(race)) is not { } rig)
            return null;
        var side = gunOf.Side;
        var parts = rig.Nodes.Select(n => new RigPart(n.Parent, n.Name)).ToList();
        var mount = RigGraft.MountOf(parts, side);
        if (mount < 0)
            return null;
        var global = new Transform3D?[rig.Nodes.Length];
        Transform3D Global(int i)
        {
            if (global[i] is { } known)
                return known;
            var parent = rig.Nodes[i].Parent;
            var place = parent >= 0 && parent < rig.Nodes.Length && parent != i ? Global(parent) * rig.Nodes[i].Rest : rig.Nodes[i].Rest;
            global[i] = place;
            return place;
        }
        var onMount = Global(mount).AffineInverse();
        var gun = new Node3D { Name = "WreckGun" };
        foreach (var part in RigGraft.Carried(parts, mount))
            if (rig.Nodes[part].Mesh is { } mesh)
                gun.AddChild(new MeshInstance3D { Mesh = mesh, Transform = onMount * Global(part) });
        if (gun.GetChildCount() == 0)
        {
            gun.Free();
            return null;
        }
        // On top of the heap, a little off its middle, turned as it fell; rolled onto its side.
        var random = new Random(id * 7919 + 17);
        var top = heap.GetChildren().OfType<MeshInstance3D>().FirstOrDefault()?.GetAabb() is { } box ? box.End.Y * 0.7f : 0.5f;
        var angle = (float)random.NextDouble() * Mathf.Tau;
        gun.Rotation = new Vector3(0, angle, 1.25f);
        gun.Position = ActorView.World(position) + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * 0.6f + Vector3.Up * (top + 0.2f);
        return gun;
    }

    /// <summary>A wreck's gun taken off: a few sparks where it lay, and it is gone (the snap is the strip's cue's).</summary>
    private void StripWreckGun(Node3D gun)
    {
        if (!IsInstanceValid(gun))
            return;
        _effects.SparkShower(gun.GlobalPosition, Vector3.Up, 24);
        gun.QueueFree();
    }
}
