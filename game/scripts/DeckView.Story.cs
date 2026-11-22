using Godot;

namespace Machines.Game;

// What of the deck's view answers the story as it goes: the ship's loudspeakers, where its announcements come from (StoryVoice
// takes the nearest), and the alarm's beacons, dark until the alarm and then turning and flashing red. The story is the
// remake's own.
public partial class DeckView
{
    private static readonly Color AlarmColour = new(1f, 0.08f, 0.04f);

    /// <summary>How fast a beacon turns while the alarm sounds, turns a second, and how far its two beams reach.</summary>
    private const float BeaconTurns = 0.9f, BeaconReach = 24;

    /// <summary>
    /// How brightly a lit beacon's glass glows (it flashes between 0.4 and 1.2 times this): bright enough to read as a
    /// red alarm light under a lit room's ceiling lamps too, where a glow of 3 was a dull red dot.
    /// </summary>
    private const float BeaconGlow = 7;

    private readonly List<Node3D> _beaconTurners = [];
    private readonly List<Light3D> _beaconBeams = [];
    private StandardMaterial3D? _beaconGlass;
    private bool _alarm;
    private double _alarmTime;

    /// <summary>Whether the ship's alarm sounds now, as the view shows it.</summary>
    public bool Alarm => _alarm;

    /// <summary>The loudspeakers (the deck's pa_speaker fittings) as points in StoryVoice's group, and the alarm's beacons.</summary>
    private void StoryFittings()
    {
        var speakers = new Node3D { Name = "Speakers" };
        AddChild(speakers);
        foreach (var piece in Layout.Pieces.Where(p => p.Prop == StoryVoice.SpeakerGroup))
        {
            var point = new Node3D { Position = Props.World(piece.Position) + Vector3.Up * 0.4f };
            speakers.AddChild(point);
            point.AddToGroup(StoryVoice.SpeakerGroup);
        }
        if (Props.Get("alarm_beacon") is not { } model)
            return;
        var beacons = new Node3D { Name = "Beacons" };
        AddChild(beacons);
        _beaconGlass = BeaconGlass();
        var glass = BeaconDome;
        foreach (var piece in Layout.Beacons)
        {
            var place = Props.Place(model, Props.World(piece.Position), piece.Angle, Vector3.One);
            beacons.AddChild(new MeshInstance3D { Mesh = model.Mesh, Transform = place, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            // The dome over the base glows red with the alarm; inside it the lamp turns, two beams back to back.
            var dome = Props.World(piece.Position) + Vector3.Up * 0.55f;
            beacons.AddChild(new MeshInstance3D { Mesh = glass, MaterialOverride = _beaconGlass, Position = dome, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            var turner = new Node3D { Position = dome, Visible = false, Rotation = new Vector3(0, piece.Angle * 0.37f, 0) };
            beacons.AddChild(turner);
            foreach (var back in new[] { 0f, Mathf.Pi })
            {
                var beam = new SpotLight3D
                {
                    LightColor = AlarmColour,
                    LightEnergy = 12,
                    SpotRange = BeaconReach,
                    SpotAngle = 22,
                    SpotAttenuation = 0.8f,
                    // Tipped down a little, so the light sweeps the floor and the walls of its room.
                    Rotation = new Vector3(-0.25f, back, 0),
                    DistanceFadeEnabled = true,
                    DistanceFadeBegin = 40,
                    DistanceFadeLength = 12,
                    LightVolumetricFogEnergy = 1.2f,
                };
                turner.AddChild(beam);
                _beaconBeams.Add(beam);
            }
            _beaconTurners.Add(turner);
        }
    }

    private static readonly SphereMesh BeaconDome = new() { Radius = 0.36f, Height = 0.56f, RadialSegments = 12, Rings = 6 };

    /// <summary>The beacons' red glass, dark until the alarm (one material for all, so they flash together).</summary>
    private static StandardMaterial3D BeaconGlass() => new()
    {
        AlbedoColor = new Color(0.35f, 0.04f, 0.03f, 0.85f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        Emission = AlarmColour,
        EmissionEnergyMultiplier = 0,
        EmissionEnabled = true,
        Roughness = 0.2f,
    };

    /// <summary>A lit beacon's dome for the warm-up, so its glass is drawn once before the alarm.</summary>
    public static MeshInstance3D WarmBeacon()
    {
        var glass = BeaconGlass();
        glass.EmissionEnergyMultiplier = BeaconGlow;
        return new MeshInstance3D { Mesh = BeaconDome, MaterialOverride = glass };
    }

    /// <summary>The ship's alarm on or off: the beacons turn and flash red while it sounds.</summary>
    public void SetAlarm(bool on)
    {
        if (_alarm == on)
            return;
        _alarm = on;
        foreach (var turner in _beaconTurners)
            turner.Visible = on;
        if (_beaconGlass is not null)
            _beaconGlass.EmissionEnergyMultiplier = on ? BeaconGlow : 0;
    }

    public override void _Process(double delta)
    {
        Tumbling((float)delta);
        Flicker(delta);
        if (!_alarm)
            return;
        _alarmTime += delta;
        var turn = (float)(delta * BeaconTurns * Mathf.Tau);
        foreach (var turner in _beaconTurners)
            turner.RotateY(turn);
        // The dome flashes as a beam passes the eye: twice a turn, never quite dark.
        if (_beaconGlass is not null)
            _beaconGlass.EmissionEnergyMultiplier = BeaconGlow * (0.4f + 0.8f * Mathf.Abs(Mathf.Sin((float)_alarmTime * BeaconTurns * Mathf.Pi)));
    }

    // ---- the crash: fires, and what falls and slides ----------------------------------------------------

    private readonly List<(GpuParticles3D Fire, GpuParticles3D Smoke, OmniLight3D Glow)> _fires = [];
    private double _flicker;

    /// <summary>How many of the crash's fires burn (DeckLayout.CrashFires, in its order): none until the countdown.</summary>
    public int FiresBurning { get; private set; }

    /// <summary>Lights the first <paramref name="count"/> of the crash's fires (and puts out the rest), made when first lit.</summary>
    public void SetFires(int count)
    {
        var places = Layout.CrashFires;
        count = Math.Clamp(count, 0, places.Count);
        while (_fires.Count < count)
        {
            var at = Props.World(places[_fires.Count]);
            var big = _fires.Count % 3 == 0;
            var fire = Effects.DamageFire(big ? 2.2f : 1.4f, big ? 3.2f : 2.2f);
            fire.Position = at + Vector3.Up * 0.2f;
            var smoke = Effects.Plume(4, 4.5f, big ? 8 : 6, big ? 3 : 2.2f, new Color(0.05f, 0.045f, 0.04f, 0.75f));
            smoke.Position = at + Vector3.Up * 2;
            var glow = new OmniLight3D
            {
                LightColor = new Color(1f, 0.5f, 0.18f),
                LightEnergy = 2.2f,
                OmniRange = big ? 10 : 7,
                Position = at + Vector3.Up * 1.5f,
                DistanceFadeEnabled = true,
                DistanceFadeBegin = 40,
                DistanceFadeLength = 10,
                LightVolumetricFogEnergy = 0.5f,
            };
            AddChild(fire);
            AddChild(smoke);
            AddChild(glow);
            _fires.Add((fire, smoke, glow));
        }
        for (var i = 0; i < _fires.Count; i++)
        {
            var (fire, smoke, glow) = _fires[i];
            fire.Emitting = smoke.Emitting = i < count;
            glow.Visible = i < count;
        }
        FiresBurning = count;
    }

    /// <summary>The fires' glow flickers.</summary>
    private void Flicker(double delta)
    {
        if (FiresBurning == 0)
            return;
        _flicker += delta;
        for (var i = 0; i < FiresBurning; i++)
        {
            var t = (float)_flicker * 9 + i * 1.7f;
            _fires[i].Glow.LightEnergy = 1.8f + 0.5f * Mathf.Sin(t) + 0.3f * Mathf.Sin(t * 2.3f + 0.4f);
        }
    }

    /// <summary>A piece the view throws or drops (a crate at the alarm, debris in the crash): it flies, lands and slides.</summary>
    private sealed class Tumbler
    {
        public required MeshInstance3D Node;
        public Vector3 Velocity, Spin;
        public float Bottom, Radius;
        public bool Resting;
    }

    /// <summary>Where the machines are that push loose pieces aside as they drive into them (Godot's frame), this frame.</summary>
    public List<Vector3> Shovers { get; } = [];

    /// <summary>How far past a piece's own half width a machine shoves it, metres, and how fast it goes.</summary>
    private const float ShoveReach = 1.6f, ShoveSpeed = 4;

    /// <summary>The way a resting piece is shoved by the nearest machine within reach, or zero.</summary>
    private Vector3 Shove(Vector3 place, float radius)
    {
        foreach (var at in Shovers)
        {
            var away = (place - at) with { Y = 0 };
            var reach = radius + ShoveReach;
            if (away.LengthSquared() >= reach * reach)
                continue;
            return (away.LengthSquared() > 0.0001f ? away.Normalized() : Vector3.Right) * ShoveSpeed;
        }
        return Vector3.Zero;
    }

    private readonly List<Tumbler> _tumblers = [];

    /// <summary>The way down the deck as the ship lists (metres a second², along the floor): what lies loose slides along it.</summary>
    public Vector3 Downhill { get; set; }

    /// <summary>The most loose pieces kept; the oldest go first.</summary>
    private const int MostTumblers = 40;

    /// <summary>
    /// Throws a prop from <paramref name="at"/> (Godot's frame) with a velocity and a spin (radians a second about each axis):
    /// it flies, falls, bounces off the floor, stops at the walls, and slides on the way down while the ship lists. View only:
    /// the world has already taken it away or never had it.
    /// </summary>
    public void Throw(string prop, Vector3 at, Vector3 velocity, Vector3 spin, float scale = 1)
    {
        if (Props.Get(prop) is not { } model)
            return;
        var node = new MeshInstance3D
        {
            Name = "Thrown",
            Mesh = model.Mesh,
            Transform = new Transform3D(Basis.FromScale(Vector3.One * scale), at) * model.Offset,
        };
        AddChild(node);
        _tumblers.Add(new Tumbler
        {
            Node = node,
            Velocity = velocity,
            Spin = spin,
            Bottom = model.Bounds.Position.Y * scale,
            Radius = Mathf.Max(model.Size.X, model.Size.Z) * scale / 2,
        });
        if (_tumblers.Count > MostTumblers)
        {
            _tumblers[0].Node.QueueFree();
            _tumblers.RemoveAt(0);
        }
    }

    private void Tumbling(float delta)
    {
        if (_tumblers.Count == 0 || delta <= 0)
            return;
        foreach (var piece in _tumblers)
        {
            var node = piece.Node;
            var place = node.Position;
            if (piece.Resting)
            {
                // Lying on the floor: a machine driving into it shoves it aside; it slides when the list is steep enough,
                // else it comes to a stop; a wall stops it.
                if (Shove(place, piece.Radius) is var shove && shove != Vector3.Zero)
                    piece.Velocity = shove;
                else if (Downhill.LengthSquared() >= 0.5f)
                    piece.Velocity = piece.Velocity.Lerp(Downhill * 0.6f, 1 - Mathf.Exp(-0.8f * delta));
                else if (piece.Velocity.LengthSquared() > 0.01f)
                    piece.Velocity *= Mathf.Exp(-4 * delta);
                else
                    continue;
            }
            else
            {
                piece.Velocity += Vector3.Down * 9.8f * delta;
                node.Basis = (node.Basis * new Basis(Quaternion.FromEuler(piece.Spin * delta))).Orthonormalized() * Basis.FromScale(node.Basis.Scale);
            }
            var next = place + piece.Velocity * delta;
            // Walls stop it: what would go into a wall cell keeps its place across that way.
            if (!Layout.IsOpen((int)MathF.Floor(next.X), (int)MathF.Floor(place.Z)))
                (next.X, piece.Velocity.X) = (place.X, -piece.Velocity.X * 0.3f);
            if (!Layout.IsOpen((int)MathF.Floor(next.X), (int)MathF.Floor(next.Z)))
                (next.Z, piece.Velocity.Z) = (place.Z, -piece.Velocity.Z * 0.3f);
            if (next.Y <= 0 && piece.Velocity.Y < 0)
            {
                next.Y = 0;
                if (piece.Velocity.Y < -2.5f)
                {
                    // A bounce: a little up, slower along, spinning less.
                    piece.Velocity = new Vector3(piece.Velocity.X * 0.5f, -piece.Velocity.Y * 0.3f, piece.Velocity.Z * 0.5f);
                    piece.Spin *= 0.5f;
                }
                else
                {
                    // Settled: flat on the floor, turned only about the up axis.
                    piece.Resting = true;
                    piece.Velocity = new Vector3(piece.Velocity.X, 0, piece.Velocity.Z) * 0.3f;
                    var scale = node.Basis.Scale;
                    var forward = node.Basis.X with { Y = 0 };
                    var yaw = forward.LengthSquared() > 0.001f ? Mathf.Atan2(-forward.Z, forward.X) : 0;
                    node.Basis = new Basis(Vector3.Up, yaw) * Basis.FromScale(scale);
                }
            }
            node.Position = next;
        }
    }
}
