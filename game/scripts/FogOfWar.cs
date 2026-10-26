using Godot;
using Machines.Import.Model;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

/// <summary>
/// The fog of war as the original drew it (MachContinentMap): the player's machines uncover the planet as they
/// go, each a circle by its scanner range (20 to 200 m across), pods 130 m and buildings once begun 80 m. When
/// the camera looks at ground nobody has been to, the whole view darkens towards half or full darkness with
/// static; in full darkness nothing there can be picked and the world falls silent. A level 1 beacon's map
/// shows only the uncovered parts; a level 3 beacon lifts the fog altogether. It is the player's view only:
/// neither the simulation nor the AI knows of it, as in the original.
/// </summary>
public partial class FogOfWar : CanvasLayer
{
    // MachContinentMap's scanner classes: the scanner range each class goes up to, and the circle it uncovers.
    private static readonly int[] ScannerUpperLimit = [20, 50, 90, 130, int.MaxValue];
    private static readonly float[] ScannerDiameter = [20, 40, 80, 130, 200];
    private const int PodScanner = 4, ConstructionScanner = 3;

    // The coarse grid that remembers the biggest scanner to have stood in each part of the planet.
    private const int BeenHereSize = 70;

    private const double UpdateInterval = 0.25;

    private enum Level { Clear, TendToHalf, TendToFull, Full }

    private readonly GameWorld _world;
    private readonly Race _player;
    private readonly float _cellSize;
    private readonly int _width, _depth;
    private readonly bool[] _uncovered;
    private readonly byte[] _beenHere = new byte[BeenHereSize * BeenHereSize];
    private readonly Image _mask;
    private ImageTexture? _maskTexture;
    private bool _maskDirty = true;
    private double _sinceUpdate = UpdateInterval;
    private float _light = 1;
    private bool _first = true;
    private ShaderMaterial _overlay = null!;

    /// <param name="cellSize">Metres per map pixel: the original uncovered and tested its map image.</param>
    public FogOfWar(GameWorld world, Race player, float cellSize)
    {
        _world = world;
        _player = player;
        _cellSize = Mathf.Max(1, cellSize);
        _width = Mathf.CeilToInt(world.Planet.Surface.Width / _cellSize);
        _depth = Mathf.CeilToInt(world.Planet.Surface.Depth / _cellSize);
        _uncovered = new bool[_width * _depth];
        _mask = Image.CreateEmpty(_width, _depth, false, Image.Format.Rgba8);
        _mask.Fill(Colors.Black);
        Name = "FogOfWar";
        // Over the 3D view, under the control panel.
        Layer = 0;
    }

    /// <summary>The player's best standing beacon: 0 for none, 1 shows the uncovered map, 3 lifts the fog.</summary>
    public int Beacon => _world.Constructions
        .Where(c => c.Race == _player && c.Complete && c.IsType("BEACON"))
        .Select(c => c.Stats.HardwareLevel)
        .DefaultIfEmpty(0)
        .Max();

    /// <summary>The view has gone dark: nothing can be picked there and the world is silent.</summary>
    public bool Blind => _light <= 0.001f;

    /// <summary>The uncovered parts of the planet for the map: black where nobody has been, clear elsewhere.</summary>
    public Texture2D Mask
    {
        get
        {
            if (_maskDirty || _maskTexture is null)
            {
                if (_maskTexture is null)
                    _maskTexture = ImageTexture.CreateFromImage(_mask);
                else
                    _maskTexture.Update(_mask);
                _maskDirty = false;
            }
            return _maskTexture;
        }
    }

    public override void _Ready()
    {
        var shader = new Shader
        {
            Code = """
                shader_type canvas_item;
                render_mode blend_premul_alpha, unshaded;
                // How dark the view is (0 to 1), and how much static (RenDevice::interferenceOn).
                uniform float darkness;
                uniform float interference;
                float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }
                void fragment() {
                    vec2 grain = floor(FRAGCOORD.xy / 2.0);
                    float noise = hash(grain + vec2(floor(TIME * 25.0) * 17.0, floor(TIME * 25.0) * 3.0));
                    float band = 0.65 + 0.35 * sin(FRAGCOORD.y * 0.08 + TIME * 40.0);
                    float speck = interference * noise * band * 0.55;
                    COLOR = vec4(vec3(speck), darkness);
                }
                """,
        };
        _overlay = new ShaderMaterial { Shader = shader };
        AddChild(new ColorRect
        {
            Material = _overlay,
            AnchorRight = 1,
            AnchorBottom = 1,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        });
        Apply();
    }

    /// <summary>
    /// Uncovers around the player's machines and buildings, and darkens the view by where the camera looks
    /// (<paramref name="lookingAt"/>, on the ground). In first person the view stays clear.
    /// </summary>
    public void Update(double delta, NVector2 lookingAt, bool firstPerson)
    {
        _sinceUpdate += delta;
        if (_sinceUpdate >= UpdateInterval)
        {
            _sinceUpdate = 0;
            Uncover();
        }

        if (firstPerson || Beacon >= 3)
        {
            _light = 1;
            Apply();
            return;
        }

        // MachContinentMap::update3dFogOfWarLightLevel: the light comes back fast, ebbs slowly towards half or
        // nothing at the edge of the known, and goes very fast over unknown ground.
        var dt = (float)delta;
        switch (LevelAt(lookingAt))
        {
            case Level.Clear:
                _light = Mathf.Min(1, _light + 0.8f * dt);
                break;
            case Level.TendToHalf:
                _light = _light > 0.5f ? Mathf.Max(0.5f, _light - 0.2f * dt) : Mathf.Min(0.5f, _light + 0.2f * dt);
                break;
            case Level.TendToFull:
                _light = Mathf.Max(0, _light - 0.2f * dt);
                break;
            default:
                _light = _first ? 0 : Mathf.Max(0, _light - 2f * dt);
                break;
        }
        _first = false;
        Apply();
    }

    private void Apply()
    {
        if (_overlay is null)
            return;
        // Below half light the picture darkens twice as fast; the static grows with the fog, then gives way to black.
        var filter = _light < 0.5f ? 2 * _light : 1;
        var intensity = 1 - _light;
        var interference = intensity < 0.7f ? intensity : 0.7f / 0.3f * (1 - intensity);
        _overlay.SetShaderParameter("darkness", 1 - filter);
        _overlay.SetShaderParameter("interference", interference);
        Visible = filter < 0.999f || interference > 0.001f;
    }

    /// <summary>MachContinentMap::getFogOfWarLevel: clear within a pixel of uncovered ground, then rings of fog.</summary>
    private Level LevelAt(NVector2 point)
    {
        var (cx, cy) = ((int)(point.X / _cellSize), (int)(point.Y / _cellSize));
        for (var ring = 0; ring <= 3; ring++)
        {
            for (var dy = -ring; dy <= ring; dy++)
            {
                for (var dx = -ring; dx <= ring; dx++)
                {
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring || !Uncovered(cx + dx, cy + dy))
                        continue;
                    return ring <= 1 ? Level.Clear : ring == 2 ? Level.TendToHalf : Level.TendToFull;
                }
            }
        }
        return Level.Full;
    }

    // Beyond the planet's edge counts as known, as the original cleared the unused parts of its map.
    private bool Uncovered(int x, int y) => x < 0 || y < 0 || x >= _width || y >= _depth || _uncovered[y * _width + x];

    private void Uncover()
    {
        var surface = _world.Planet.Surface;
        foreach (var actor in _world.Actors)
        {
            if (actor.Race != _player)
                continue;
            var scanner = actor switch
            {
                Machine { Inside: not null } => 0,
                Machine machine => Array.FindIndex(ScannerUpperLimit, limit => machine.Stats.ScannerRange <= limit) + 1,
                Construction pod when pod.IsType("POD") => PodScanner,
                // A building only uncovers once work on it has begun, so a laid-out site cannot scout.
                Construction site => site.Built > 0 ? ConstructionScanner : 0,
                _ => 0,
            };
            if (scanner < 1)
                continue;

            // Only a bigger scanner than any before in this part of the planet uncovers more.
            var bx = Math.Clamp((int)(actor.Position.X / surface.Width * BeenHereSize), 0, BeenHereSize - 1);
            var by = Math.Clamp((int)(actor.Position.Y / surface.Depth * BeenHereSize), 0, BeenHereSize - 1);
            ref var been = ref _beenHere[by * BeenHereSize + bx];
            if (been >= scanner)
                continue;
            been = (byte)scanner;
            UncoverCircle(actor.Position, Mathf.Max(ScannerDiameter[scanner - 1], 6 * _cellSize) / 2);
        }
    }

    private void UncoverCircle(NVector2 centre, float radius)
    {
        var (x0, x1) = ((int)((centre.X - radius) / _cellSize), (int)((centre.X + radius) / _cellSize));
        var (y0, y1) = ((int)((centre.Y - radius) / _cellSize), (int)((centre.Y + radius) / _cellSize));
        for (var y = Math.Max(0, y0); y <= Math.Min(_depth - 1, y1); y++)
        {
            for (var x = Math.Max(0, x0); x <= Math.Min(_width - 1, x1); x++)
            {
                var cell = new NVector2((x + 0.5f) * _cellSize, (y + 0.5f) * _cellSize);
                if (_uncovered[y * _width + x] || NVector2.DistanceSquared(cell, centre) > radius * radius)
                    continue;
                _uncovered[y * _width + x] = true;
                _mask.SetPixel(x, y, Colors.Transparent);
                _maskDirty = true;
            }
        }
    }
}
