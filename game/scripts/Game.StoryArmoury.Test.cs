using Godot;
using Machines.Sim.Story;

namespace Machines.Game;

// MACHINES_STORY_ARMOURY_TEST=1 (with MACHINES_MAP=prolog and MACHINES_STORY=prolog,9 or 10): once section 6 shows, a run
// through package W's view and sound, by the view alone (nothing is sent to the sim): the lockdown (klaxon, bolts, the lamps
// dipping), a batch at its doors, gate N grinding open, the workbench's strip, mount and mend held for 3 s each, the end
// sounds of a mend, a mount and a strip, a wreck laid with its gun 8 m ahead that is stripped bare 3 s later in sparks, Old
// Iron's motor, rev, scrape, crash with its stun and recovery, the lift's hum, an electric unit's crackle, gate N straining
// and the power dip at the end. Each step prints "armoury test: <seconds> <step>"; with MACHINES_AUDIO_TRACE=1 every sound
// it plays prints its "audio story-sfx" line too. The run goes on as it was meanwhile; nothing quits.
public partial class Game
{
    private static readonly bool ArmouryTest = System.Environment.GetEnvironmentVariable("MACHINES_STORY_ARMOURY_TEST") == "1";

    /// <summary>The test's held sounds have their own keys, so the armoury's own (Keep) leaves them alone.</summary>
    private const string ArmouryTestHeld = "armoury-test:";

    private float? _armouryTestStart;
    private int _armouryTestStep;
    private Node3D? _armouryTestHeap;

    /// <summary>Each tick of section 6: the test's next step once its time has come (see the file's head).</summary>
    private void ArmouryTestIfAsked(StoryRun run)
    {
        if (!ArmouryTest || _armouryTestStep >= ArmouryTestSteps.Length)
            return;
        _armouryTestStart ??= World.Time;
        var seconds = World.Time - _armouryTestStart.Value;
        while (_armouryTestStep < ArmouryTestSteps.Length && ArmouryTestSteps[_armouryTestStep].At <= seconds)
        {
            var (at, name) = ArmouryTestSteps[_armouryTestStep++];
            GD.Print($"armoury test: {at:0.0} {name}");
            ArmouryTestDo(run, name);
        }
    }

    private static readonly (float At, string Name)[] ArmouryTestSteps =
    [
        (1, "lockdown"), (4, "batch at its doors"), (6, "gate N open"),
        (9, "strip held"), (12.5f, "mount held"), (16, "mend held"), (19.5f, "workbench done"),
        (21, "wreck with its gun"), (24, "wreck stripped"),
        (27, "boss motor"), (28, "boss rev"), (29.5f, "boss run"), (31, "boss crash"), (34, "boss recover"),
        (36, "lift hum"), (39.5f, "electric crackle"), (43, "gate N strain"), (45.5f, "power dip"), (50, "done"),
    ];

    private void ArmouryTestDo(StoryRun run, string step)
    {
        var sounds = StoryAudio.Current?.Sounds;
        var camera = GetViewport()?.GetCamera3D();
        var forward = camera is null ? Vector3.Forward : (-camera.GlobalBasis.Z with { Y = 0 }).Normalized();
        var eye = camera?.GlobalPosition ?? Vector3.Zero;
        var ahead = (eye with { Y = 0 }) + forward * 8;
        var room = World.Deck?.Zone("R13") is { } r13 ? ActorView.World((r13.Min + r13.Max) / 2) : ahead;
        var gate = World.Deck?.Markers.FirstOrDefault(m => m.Name == "wave_n_1") is { } n ? ActorView.World(n.Position) : ahead;
        void Held(string id, float db = 0, float pitch = 1) =>
            sounds?.Keep(ArmouryTestHeld, new Dictionary<string, StorySounds.Held> { [ArmouryTestHeld + "one"] = new(id, ahead + Vector3.Up, db, pitch) });
        void Quiet() => sounds?.Keep(ArmouryTestHeld, new Dictionary<string, StorySounds.Held>());
        switch (step)
        {
            case "lockdown":
                StoryCueArmoury(["lockdown"], room);
                break;
            case "batch at its doors":
                StoryCueArmoury(["wave", "2", "alive"], room);
                break;
            case "gate N open":
                StoryCueArmoury(["gate", "N", "open"], gate);
                break;
            case "strip held":
                Held("strip_work", -2);
                break;
            case "mount held":
                Held("grunt_reassemble", -2);
                break;
            case "mend held":
                Held("revive_weld", -5);
                break;
            case "workbench done":
                Quiet();
                StoryCueArmoury(["mended", "1"], ahead);
                GetTree().CreateTimer(0.6).Timeout += () => StoryCueArmoury(["mounted", "1"], ahead);
                GetTree().CreateTimer(1.2).Timeout += () => StoryCueArmoury(["stripped", "0", "bolter"], ahead);
                break;
            case "wreck with its gun":
                _armouryTestHeap = new Node3D { Name = "ArmouryTestWreck" };
                if (_assets.EffectMesh("debris") is { } heap)
                    _armouryTestHeap.AddChild(new MeshInstance3D { Mesh = heap, Position = ahead });
                AddChild(_armouryTestHeap);
                var spot = new System.Numerics.Vector2(ahead.X, ahead.Z);
                if (WreckGun(StoryWaveKind.Bolter, run.Options.Ship, spot, 1, _armouryTestHeap) is { } gun)
                    _armouryTestHeap.AddChild(gun);
                else
                    GD.Print("armoury test: no gun (the Grunt's export or its mount is missing)");
                break;
            case "wreck stripped":
                if (_armouryTestHeap?.GetNodeOrNull<Node3D>("WreckGun") is { } lying)
                    StripWreckGun(lying);
                StoryAudio.Play("strip_snap", ahead);
                break;
            case "boss motor":
                Held("boss_idle", -3);
                break;
            case "boss rev":
                Held("boss_idle", -3, 1.2f);
                StoryCueArmoury(["boss", "rev", "0"], ahead);
                break;
            case "boss run":
                sounds?.Keep(ArmouryTestHeld, new Dictionary<string, StorySounds.Held>
                {
                    [ArmouryTestHeld + "one"] = new("boss_idle", ahead + Vector3.Up, -3),
                    [ArmouryTestHeld + "run"] = new("container_scrape", ahead + Vector3.Up, 2, 0.8f),
                });
                break;
            case "boss crash":
                Held("boss_idle", -8);
                StoryCueArmoury(["boss", "crash"], ahead);
                break;
            case "boss recover":
                Held("boss_idle", -3);
                StoryCueArmoury(["boss", "recover"], ahead);
                break;
            case "lift hum":
                sounds?.Keep(ArmouryTestHeld, new Dictionary<string, StorySounds.Held> { [ArmouryTestHeld + "lift"] = new("lift_hum", gate + Vector3.Up * 3, -4) });
                break;
            case "electric crackle":
                Held("electric_crackle", -6);
                break;
            case "gate N strain":
                Quiet();
                StoryCueArmoury(["gate", "N", "strain", "2"], gate);
                break;
            case "power dip":
                StoryCueArmoury(["power", "dip"], room);
                break;
            default:
                Quiet();
                _armouryTestHeap?.QueueFree();
                _armouryTestHeap = null;
                break;
        }
    }
}
