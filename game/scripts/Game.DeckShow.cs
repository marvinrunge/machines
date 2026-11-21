using Godot;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Game;

// What the view shows of the story on the deck beyond its walls and lamps (the remake's own): a Grunt knocked down lies on
// its side, sparking and smoking, with a ring on the floor that fills as the Locator puts it together; a crate rides on the
// Grunt that carries it; the barricade's crates fly when the assault bursts in; the alarm's beacons turn; the pods wait on
// their cradles; and as the ship goes down the crash's stages light fires, shake the hull, rain sparks and drop debris that
// slides to the low side. All of it follows the world (its flags, timers and cues) and changes nothing in it.
public partial class Game
{
    /// <summary>
    /// The crash's stages as the view shows them, by the seconds left on the countdown (the director's stages at 120, 60, 30
    /// and 10 s and the hull breach at 90, Prologue.cs): from the most seconds down, the first whose bound is under the time
    /// left. The first is the Notgang's run-up before the director's first stage at 120 s: the ship already groans and burns
    /// a little. Each gives the hull's rumble (degrees), the share of the fires burning, the seconds between showers of
    /// sparks, and between pieces falling.
    /// </summary>
    internal static readonly (float Above, float Rumble, float Fires, float Sparks, float Drops)[] CrashStages =
    [
        (120, 0.25f, 0.15f, 5.0f, 18),
        (90, 0.35f, 0.25f, 4.0f, 14),
        (60, 0.55f, 0.45f, 2.5f, 9),
        (30, 0.8f, 0.7f, 1.6f, 6),
        (10, 1.1f, 0.9f, 1.0f, 4),
        (float.MinValue, 1.5f, 1, 0.6f, 2.5f),
    ];

    /// <summary>Which crash stage the countdown is at (1 to 6: 1 before 120 s left, 6 in the last 10), or 0 while it does not run.</summary>
    internal static int CrashStage(float? secondsLeft)
    {
        if (secondsLeft is not { } left)
            return 0;
        for (var i = 0; i < CrashStages.Length; i++)
            if (left > CrashStages[i].Above)
                return i + 1;
        return CrashStages.Length;
    }

    /// <summary>How far a downed Grunt lies over, degrees.</summary>
    private const float DownedTip = 24;

    private sealed class DownedView
    {
        public required GpuParticles3D Smoke;
        public required MeshInstance3D Ring;
        public required ShaderMaterial RingMaterial;
        public float Tip;
        public double NextSparks;
    }

    private readonly Dictionary<int, DownedView> _downedViews = [];
    private readonly Dictionary<int, Node3D> _pods = [];
    private int _crashStage;
    private bool _alarmShown;
    private double _nextCrashSparks, _nextCrashDrop;

    /// <summary>A match on a deck starts: the pods on their cradles, the tick's cues heard (<see cref="DeckTicked"/>).</summary>
    private void StartDeckShow()
    {
        Ticked -= DeckTicked;
        Ticked += DeckTicked;
        _downedViews.Clear();
        _pods.Clear();
        _crashStage = 0;
        _alarmShown = false;
        if (_deckView is null || World.Deck is not { } deck)
            return;
        // The game's pods (POD) waiting on their cradles; the one that is launched is the cutscene's own as it drops.
        foreach (var marker in deck.Markers.Where(m => m.Tags.Contains("pod")))
        {
            if (SceneModel("pod") is not { } pod)
                break;
            pod.Name = marker.Name;
            pod.Position = Props.World(new System.Numerics.Vector3(marker.Position, 0));
            pod.Rotation = new Vector3(0, Mathf.DegToRad(90 - marker.Angle), 0);
            // The pod (a building of the game, made to stand on a planet) is shown as big as its cradle's ring holds.
            var size = Extent(pod, Transform3D.Identity);
            var scale = Mathf.Min(PodWidth / Mathf.Max(size.Size.X, size.Size.Z), 1);
            if (_pods.Count == 0)
                GD.Print($"deck: pod model {size.Size} shown at {scale:0.###}");
            pod.Scale = Vector3.One * scale;
            _deckView.AddChild(pod);
            _pods[_pods.Count] = pod;
            pod.SetMeta("marker", marker.Name);
        }
    }

    /// <summary>How wide a pod is shown on its cradle (pod_cradle's ring is 10 m across), metres.</summary>
    internal const float PodWidth = 7;

    /// <summary>The bounds of the meshes under a node, in the frame <paramref name="frame"/> puts it in.</summary>
    private static Aabb Extent(Node node, Transform3D frame)
    {
        var box = new Aabb();
        var any = false;
        if (node is MeshInstance3D { Mesh: { } mesh })
        {
            box = frame * mesh.GetAabb();
            any = true;
        }
        foreach (var child in node.GetChildren())
        {
            var inner = Extent(child, child is Node3D spatial ? frame * spatial.Transform : frame);
            if (inner.Size == Vector3.Zero)
                continue;
            box = any ? box.Merge(inner) : inner;
            any = true;
        }
        return box;
    }

    /// <summary>What a tick's story events show on the deck: the alarm, the barricade's crates flying, the pod leaving its cradle.</summary>
    private void DeckTicked()
    {
        if (_deckView is null || World.Story is null)
            return;
        foreach (var e in World.Events)
        {
            switch (e.Kind)
            {
                case GameEventKind.StoryAlarm:
                    _deckView.SetAlarm(e.Other == 1);
                    break;
                case GameEventKind.StoryCue when StoryEvents.CueOf(e) is var words:
                    if (words is ["thrown", _, ..])
                        ThrowCrate(ActorView.World(e.Position), words.Length > 2 ? words[2] : null);
                    else if (words is ["cutscene", "abwurf"])
                        foreach (var pod in _pods.Values.Where(p => p.GetMeta("marker").AsString() == "pod_3"))
                            pod.Visible = false;
                    break;
            }
        }
    }

    /// <summary>
    /// A crate of the barricade thrown aside as the assault bursts through its door: it flies away from the doorway into the
    /// room, tumbling, lands and slides: the crate of the type that stood there (the pile has large and small ones).
    /// </summary>
    private void ThrowCrate(Vector3 at, string? type)
    {
        if (_deckView is null)
            return;
        var place = new System.Numerics.Vector2(at.X, at.Z);
        var door = _deckView.Layout.Openings.Where(o => o.Kind != "grate")
            .MinBy(o => System.Numerics.Vector2.DistanceSquared(o.Rect.Centre, place));
        var away = door is null ? System.Numerics.Vector2.UnitY : place - door.Rect.Centre;
        away = away.LengthSquared() > 0.01f ? System.Numerics.Vector2.Normalize(away) : System.Numerics.Vector2.UnitY;
        var random = new RandomNumberGenerator();
        var speed = random.RandfRange(7, 11);
        var velocity = new Vector3(away.X * speed + random.RandfRange(-2, 2), random.RandfRange(4, 6.5f), away.Y * speed + random.RandfRange(-2, 2));
        var spin = new Vector3(random.RandfRange(-5, 5), random.RandfRange(-3, 3), random.RandfRange(-5, 5));
        _deckView.Throw(type is null ? "crate_large" : Props.ForArtefact(type) ?? "crate_large", at + Vector3.Up * 0.3f, velocity, spin);
        _effects.SparkShower(at + Vector3.Up * 1.2f, new Vector3(away.X, 0.6f, away.Y), 30);
    }

    /// <summary>Each frame of a story on a deck: the alarm's beacons, the Grunts down or carrying, the crash.</summary>
    private void ShowDeckStory(double delta)
    {
        if (_deckView is null || World.Story is not { } story)
            return;
        // Also as the match comes up at a checkpoint after the alarm: the beacons follow the run's flag when it changes.
        if (story.Flags.Contains(StoryMarks.Alarm) is var alarm && alarm != _alarmShown)
        {
            _alarmShown = alarm;
            _deckView.SetAlarm(alarm);
        }
        for (var i = 1; i < story.Seats.Count; i++)
            ShowGrunt(story, story.Seats[i], delta);
        // The trio pushes what lies loose out of its way (it is the view's only; the world has no such pieces).
        _deckView.Shovers.Clear();
        foreach (var seat in story.Seats)
            if (World.HeroOf(seat) is { } hero && _views.TryGetValue(hero.Id, out var view))
                _deckView.Shovers.Add(view.GlobalPosition);
        ShowCrash(story, delta);
    }

    /// <summary>A Grunt of the trio: lying tipped, sparking and smoking while it is down, its ring filling as it is revived; a crate it carries on its back.</summary>
    private void ShowGrunt(StoryRun story, PlayerId seat, double delta)
    {
        if (World.HeroOf(seat) is not { } grunt || !_views.TryGetValue(grunt.Id, out var view))
            return;
        var companion = story.Crew.Grunts.GetValueOrDefault(seat.Value);
        view.Carry(companion?.Carrying is { } crate ? Props.ForArtefact(crate.Type.Name) ?? "crate_large" : null);
        var down = World.CompanionDown(seat);
        if (!_downedViews.TryGetValue(grunt.Id, out var shown))
        {
            if (!down)
                return;
            shown = DownedViewOf(view);
            _downedViews[grunt.Id] = shown;
        }
        shown.Tip = Mathf.MoveToward(shown.Tip, down ? DownedTip : 0, (float)delta * (down ? 60 : 30));
        view.Tip(shown.Tip);
        shown.Smoke.Emitting = down;
        shown.Ring.Visible = down;
        if (down)
        {
            // The revive by the Locator's hold, or the repair station's: whichever is further.
            var revive = companion is null ? 0 : Math.Max(
                companion.ReviveTicks / (GameWorld.ReviveSeconds * GameWorld.TicksPerSecond),
                companion.RepairTicks / (GameWorld.RepairReviveSeconds * GameWorld.TicksPerSecond));
            shown.Ring.GlobalPosition = view.GlobalPosition with { Y = 0.06f };
            shown.RingMaterial.SetShaderParameter("progress", Mathf.Clamp(revive, 0, 1));
            shown.RingMaterial.SetShaderParameter("pulse", (float)(FrameClock.Now % 1.0));
            if (FrameClock.Now >= shown.NextSparks)
            {
                shown.NextSparks = FrameClock.Now + GD.RandRange(0.5, 1.5);
                var side = new Vector3((float)GD.RandRange(-1.0, 1.0), 0.8f, (float)GD.RandRange(-1.0, 1.0));
                _effects.SparkShower(view.Centre + Vector3.Up * 0.4f, side, 14);
            }
        }
        else if (shown.Tip == 0)
        {
            shown.Smoke.QueueFree();
            shown.Ring.QueueFree();
            _downedViews.Remove(grunt.Id);
        }
    }

    private static readonly Lazy<Shader> RingShader = new(() => new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, blend_mix, depth_draw_never, cull_disabled, shadows_disabled;
            uniform float progress = 0.0;
            uniform float pulse = 0.0;
            uniform vec4 lit : source_color = vec4(0.3, 1.0, 0.45, 0.9);
            uniform vec4 dim : source_color = vec4(1.0, 0.25, 0.1, 0.35);
            void fragment() {
                vec2 p = UV * 2.0 - 1.0;
                float r = length(p);
                float band = smoothstep(0.80, 0.84, r) * (1.0 - smoothstep(0.95, 0.99, r));
                float a = atan(p.x, -p.y) / 6.2831853 + 0.5;
                vec4 c = a <= progress ? lit : dim;
                // The part still to fill breathes, so the ring reads as waiting for the Locator.
                float breathe = a <= progress ? 1.0 : 0.6 + 0.4 * sin(pulse * 6.2831853);
                ALBEDO = c.rgb * 2.0;
                ALPHA = band * c.a * breathe;
            }
            """,
    });

    /// <summary>
    /// What only the story's deck shows, for the warm-up to draw once before the match: a beacon's lit glass and a downed
    /// Grunt's revive ring (the fires, smoke, sparks and props are warmed with the rest).
    /// </summary>
    public static IEnumerable<MeshInstance3D> DeckShowPieces() =>
    [
        DeckView.WarmBeacon(),
        new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(2, 2) },
            MaterialOverride = new ShaderMaterial { Shader = RingShader.Value },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        },
    ];

    private DownedView DownedViewOf(ActorView view)
    {
        var smoke = Effects.Plume(3, 3, 5, 1.4f, new Color(0.08f, 0.08f, 0.08f, 0.7f));
        smoke.Position = new Vector3(view.Bounds.GetCenter().X, view.Top * 0.7f, view.Bounds.GetCenter().Z);
        view.AddChild(smoke);
        var material = new ShaderMaterial { Shader = RingShader.Value };
        var reach = GameWorld.ReviveReach;
        var ring = new MeshInstance3D
        {
            Name = "ReviveRing",
            Mesh = new PlaneMesh { Size = new Vector2(reach * 2, reach * 2) },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            TopLevel = true,
        };
        view.AddChild(ring);
        ring.GlobalPosition = view.GlobalPosition with { Y = 0.06f };
        return new DownedView { Smoke = smoke, Ring = ring, RingMaterial = material };
    }

    /// <summary>
    /// The crash as the countdown runs (its stage by <see cref="CrashStages"/>): the hull's rumble, the fires, showers of
    /// sparks round the Locator and pieces falling ahead of it, and loose pieces sliding as the view leans.
    /// </summary>
    private void ShowCrash(StoryRun story, double delta)
    {
        var left = story.Timers.TryGetValue(StoryMarks.Countdown, out var ends) ? ends - World.Time : (float?)null;
        var stage = CrashStage(left);
        var lean = TiltNow;
        // The ship lists to the right of the view's roll, its nose down by its pitch; loose things go that way.
        _deckView!.Downhill = new Vector3(Mathf.Sin(Mathf.DegToRad(lean.Roll)), 0, Mathf.Sin(Mathf.DegToRad(-lean.Pitch))) * 9.8f;
        if (stage != _crashStage)
        {
            // When the countdown stops (the pod is launched) the cutscene has the hull's rumble; the fires burn on.
            if (stage > 0)
            {
                CameraRig.Rumble = CrashStages[stage - 1].Rumble;
                _deckView.SetFires((int)MathF.Ceiling(_deckView.Layout.CrashFires.Count * CrashStages[stage - 1].Fires));
                _nextCrashSparks = FrameClock.Now + 0.5;
                if (stage > _crashStage)
                    _nextCrashDrop = FrameClock.Now + 0.8;
            }
            _crashStage = stage;
        }
        if (stage == 0 || InCutscene || World.HeroOf(Me) is not { } hero)
            return;
        var (_, _, _, sparks, drops) = CrashStages[stage - 1];
        var eye = ActorView.World(hero.Position);
        var heading = new Vector3(Mathf.Cos(_aimYaw), 0, Mathf.Sin(_aimYaw));
        if (FrameClock.Now >= _nextCrashSparks)
        {
            _nextCrashSparks = FrameClock.Now + sparks * GD.RandRange(0.6, 1.4);
            // From under the ceiling somewhere ahead and to a side: a torn cable, a burst pipe.
            var at = eye + heading * (float)GD.RandRange(4.0, 12.0) + heading.Cross(Vector3.Up) * (float)GD.RandRange(-4.0, 4.0);
            if (_deckView.Layout.IsOpen((int)MathF.Floor(at.X), (int)MathF.Floor(at.Z)))
            {
                var ceiling = _deckView.Layout.Height((int)MathF.Floor(at.X), (int)MathF.Floor(at.Z));
                _effects.SparkShower(at with { Y = Mathf.Max(2.5f, ceiling - 0.6f) }, null, 20 + 10 * stage);
            }
        }
        if (FrameClock.Now >= _nextCrashDrop)
        {
            _nextCrashDrop = FrameClock.Now + drops * GD.RandRange(0.7, 1.3);
            DropDebris(eye, heading, stage);
        }
    }

    // What falls from the ceiling in the crash, and how big it is shown.
    private static readonly (string Prop, float Scale)[] Falling = [("debris_panel", 0.45f), ("debris_beam", 0.35f), ("crate_small", 0.6f), ("debris_panel", 0.3f)];

    /// <summary>A piece of the ceiling or a loose crate falls somewhere ahead of the Locator, with sparks, and slides as the ship lists.</summary>
    private void DropDebris(Vector3 eye, Vector3 heading, int stage)
    {
        var at = eye + heading * (float)GD.RandRange(6.0, 14.0) + heading.Cross(Vector3.Up) * (float)GD.RandRange(-3.0, 3.0);
        var (x, y) = ((int)MathF.Floor(at.X), (int)MathF.Floor(at.Z));
        if (!_deckView!.Layout.IsOpen(x, y) || World.Deck?.Cell(x, y) is Machines.Import.Model.DeckCell.Door or Machines.Import.Model.DeckCell.Duct)
            return;
        var ceiling = _deckView.Layout.Height(x, y);
        var (prop, scale) = Falling[(int)(GD.Randi() % Falling.Length)];
        var spin = new Vector3((float)GD.RandRange(-3.0, 3.0), (float)GD.RandRange(-2.0, 2.0), (float)GD.RandRange(-3.0, 3.0));
        _deckView.Throw(prop, at with { Y = Mathf.Max(3, ceiling - 1) }, new Vector3(0, -1, 0), spin, scale * (CrashStages[stage - 1].Above <= 10 ? 1.2f : 1));
        _effects.SparkShower(at with { Y = Mathf.Max(3, ceiling - 0.5f) }, null, 40);
        StoryAudio.Play("crate_fall", at);
    }
}
