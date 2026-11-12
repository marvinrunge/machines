using System.Diagnostics;
using Godot;

namespace Machines.Game;

/// <summary>
/// For finding stutter (MACHINES_HITCH=1, or the milliseconds a frame may take, 25 by default): prints every frame of
/// the match that took longer, or in which the GPU had to wait for a pipeline Godot built for something drawn the
/// first time, with what happened in it: the pipelines built (by why: canvas, mesh, surface, draw), the garbage
/// collections, and what was loaded the first time (a model, texture, sound ...) with its milliseconds. Until the
/// match is warmed up (<see cref="Warmup"/>) nothing is printed; after, every such load and pipeline is one the
/// warm-up missed. When the game ends it sums them up.
/// </summary>
public partial class HitchLog : Node
{
    /// <summary>The longest frame that is not a hitch, in milliseconds; 0 while the log is off.</summary>
    private static readonly double Limit = System.Environment.GetEnvironmentVariable("MACHINES_HITCH") is { Length: > 0 } asked
        ? double.TryParse(asked, System.Globalization.CultureInfo.InvariantCulture, out var ms) && ms > 1 ? ms : 25
        : 0;

    public static bool On => Limit > 0;

    /// <summary>Whether the match has begun: what is loaded or built from now on, the warm-up missed.</summary>
    public static bool Playing { get; set; }

    private static readonly List<string> Loads = [];

    // The pipelines that hold a frame up until they are built, and the ones built in the background.
    private static readonly (Performance.Monitor Monitor, string Name)[] Stalling =
    [
        (Performance.Monitor.PipelineCompilationsCanvas, "canvas"),
        (Performance.Monitor.PipelineCompilationsMesh, "mesh"),
        (Performance.Monitor.PipelineCompilationsSurface, "surface"),
        (Performance.Monitor.PipelineCompilationsDraw, "draw"),
    ];

    private readonly long[] _compiled = new long[Stalling.Length];
    private readonly int[] _collections = new int[3];
    private ulong _last;
    private int _hitches, _pipelines, _cold;
    private double _worst, _usual;

    public static HitchLog? Create() => On ? new HitchLog { Name = "HitchLog", ProcessMode = ProcessModeEnum.Always, ProcessPriority = int.MinValue } : null;

    /// <summary>A moment to measure a first load from (<see cref="Loaded"/>).</summary>
    public static long Start => On ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Notes that <paramref name="what"/> was loaded the first time, from <paramref name="start"/> until now.</summary>
    public static void Loaded(string what, long start)
    {
        if (!On)
            return;
        var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        // The warm-up loads on several threads.
        lock (Loads)
        {
            if (Playing)
                Loads.Add($"{what} {milliseconds:F1} ms");
            else
            {
                var kind = what.Split(' ')[0];
                Before[kind] = Before.GetValueOrDefault(kind) + milliseconds;
            }
        }
    }

    // Milliseconds spent loading each kind of thing before the match began (a texture's are also its model's).
    private static readonly Dictionary<string, double> Before = [];

    /// <summary>What the loading before the match began cost, by kind, while the log is on.</summary>
    public static string LoadedBefore() =>
        string.Join(", ", Before.OrderByDescending(b => b.Value).Select(b => $"{b.Key} {b.Value / 1000:F1} s"));

    // The meshes, particles and decals that came into the scene this frame: what a pipeline built in it was for.
    private readonly List<Node> _added = [];

    private void Added(Node node)
    {
        if (Playing && node is GeometryInstance3D or Decal && _added.Count < 12)
            _added.Add(node);
    }

    // What a node is drawn with, as far as it tells which pipeline it needs: its material's kind, blending and shading.
    private static string Drawn(Node node)
    {
        var material = node switch
        {
            GeometryInstance3D { MaterialOverride: { } given } => given,
            MeshInstance3D { Mesh: { } mesh } instance when mesh.GetSurfaceCount() > 0 => instance.GetActiveMaterial(0),
            GpuParticles3D { DrawPass1: { } pass } when pass.GetSurfaceCount() > 0 => pass.SurfaceGetMaterial(0),
            _ => null,
        };
        return material switch
        {
            BaseMaterial3D standard => $" [{standard.Transparency} {standard.ShadingMode} {standard.BillboardMode}{(standard.VertexColorUseAsAlbedo ? " vertex" : "")}]",
            ShaderMaterial shader => $" [{(shader.Shader?.ResourcePath is { Length: > 0 } path ? path : "shader")}]",
            null => "",
            _ => $" [{material.GetType().Name}]",
        };
    }

    /// <summary>The pipelines that hold frames up, built so far.</summary>
    public static long Compiled() => Stalling.Sum(s => (long)Performance.GetMonitor(s.Monitor));

    public override void _Ready()
    {
        Playing = false;
        Loads.Clear();
        Before.Clear();
        Measure();
        GetTree().NodeAdded += Added;
    }

    // Runs first in each frame: the time since the last one is the frame before, its process and its drawing.
    public override void _Process(double delta)
    {
        var (milliseconds, built, collected) = Measure();
        // Told as they are drawn: a mesh or material is often given just after the node came in.
        var added = _added.Where(IsInstanceValid).Select(n => $"{n.GetParent()?.Name}/{n.Name}{Drawn(n)}").ToList();
        _added.Clear();
        if (!Playing)
        {
            Loads.Clear();
            return;
        }
        // A frame is a hitch when it takes longer than the limit and twice as long as the frames before it.
        var usual = _usual;
        _usual = _usual == 0 ? milliseconds : _usual * 0.95 + Math.Min(milliseconds, 2 * _usual) * 0.05;
        var slow = milliseconds > Limit && milliseconds > 2 * usual;
        var stalled = built.Sum(b => b.Count);
        if (!slow && stalled == 0 && Loads.Count == 0)
            return;
        var line = $"hitch: {milliseconds:F1} ms (usual {usual:F1})";
        if (stalled > 0)
            line += $", pipelines {string.Join(", ", built.Where(b => b.Count > 0).Select(b => $"{b.Name} {b.Count}"))} for {string.Join(" ", added)}";
        if (collected.Length > 0)
            line += $", gc {collected}";
        if (Loads.Count > 0)
            line += $", loaded {string.Join(", ", Loads)}";
        GD.Print(line);
        _hitches += slow ? 1 : 0;
        _worst = Math.Max(_worst, milliseconds);
        _pipelines += stalled;
        _cold += Loads.Count;
        Loads.Clear();
    }

    private (double Milliseconds, List<(string Name, int Count)> Built, string Collected) Measure()
    {
        var now = Time.GetTicksUsec();
        var milliseconds = _last == 0 ? 0 : (now - _last) / 1000.0;
        _last = now;
        var built = new List<(string, int)>();
        for (var i = 0; i < Stalling.Length; i++)
        {
            var count = (long)Performance.GetMonitor(Stalling[i].Monitor);
            built.Add((Stalling[i].Name, (int)(count - _compiled[i])));
            _compiled[i] = count;
        }
        var collected = new List<string>();
        for (var generation = 0; generation < _collections.Length; generation++)
        {
            var count = GC.CollectionCount(generation);
            if (count != _collections[generation])
                collected.Add($"gen{generation} {count - _collections[generation]}");
            _collections[generation] = count;
        }
        return (milliseconds, built, string.Join(" ", collected));
    }

    public override void _ExitTree()
    {
        if (Playing)
            GD.Print($"hitch summary: {_hitches} frames over {Limit:F0} ms (worst {_worst:F1} ms), {_pipelines} pipelines built while playing, {_cold} first loads while playing");
        Playing = false;
    }
}
