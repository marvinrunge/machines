using Godot;

namespace Machines.Game;

/// <summary>
/// For measuring (MACHINES_PERF=<seconds>): after a few seconds to settle, logs once a second what a frame costs (its
/// time on the CPU and on the GPU, and the frame rate) and what is drawn (objects, draw calls, triangles), then sums
/// it up and ends the game. Run it with --disable-vsync, or the frame rate stops at the screen's.
/// </summary>
public partial class PerfLog : Node
{
    private const double Settle = 5;

    private double _seconds, _time, _next = Settle;
    private readonly List<(double Fps, double Cpu, double Gpu, long Objects, long Calls, long Triangles)> _samples = [];

    public static PerfLog? Create() =>
        double.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_PERF"), System.Globalization.CultureInfo.InvariantCulture, out var s)
            ? new PerfLog { Name = "PerfLog", _seconds = s, ProcessMode = ProcessModeEnum.Always }
            : null;

    public override void _Ready() => RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);

    public override void _Process(double delta)
    {
        if (_time == 0)
            SwitchOff(System.Environment.GetEnvironmentVariable("MACHINES_PERF_OFF")?.Split(',') ?? []);
        _time += delta;
        if (_time < _next)
            return;
        _next += 1;
        var viewport = GetViewport().GetViewportRid();
        var sample = (
            Engine.GetFramesPerSecond(),
            RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport) + RenderingServer.GetFrameSetupTimeCpu(),
            RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport),
            (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame),
            (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame),
            (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame));
        _samples.Add(sample);
        GD.Print($"perf: {sample.Item1:F0} fps, render cpu {sample.Item2:F2} ms, gpu {sample.Item3:F2} ms, {sample.Item4} objects, {sample.Item5} draw calls, {sample.Item6 / 1000} k triangles");
        if (_time < Settle + _seconds)
            return;
        var n = _samples.Count;
        GD.Print($"perf summary: {_samples.Average(s => s.Fps):F0} fps (lowest {_samples.Min(s => s.Fps):F0}), "
            + $"render cpu {_samples.Average(s => s.Cpu):F2} ms, gpu {_samples.Average(s => s.Gpu):F2} ms (worst {_samples.Max(s => s.Gpu):F2}), "
            + $"{_samples.Average(s => s.Objects):F0} objects, {_samples.Average(s => s.Calls):F0} draw calls, {_samples.Average(s => s.Triangles) / 1000:F0} k triangles over {n} s");
        GetTree().Quit();
    }

    /// <summary>
    /// To find what costs (MACHINES_PERF_OFF=ssao,shadows,...): switches off screen effects (ssao, ssil, ssr, glow,
    /// volfog, fog), the sun's shadows, the multisampling (msaa), or hides a part of the scene by its node's name
    /// (GroundScatter, Scenery, Weather, Artefacts ...).
    /// </summary>
    private void SwitchOff(string[] parts)
    {
        var all = GetTree().Root.FindChildren("*", "", true, false);
        var environment = all.OfType<WorldEnvironment>().FirstOrDefault()?.Environment;
        foreach (var part in parts)
        {
            switch (part)
            {
                case "ssao" when environment is not null: environment.SsaoEnabled = false; break;
                case "ssil" when environment is not null: environment.SsilEnabled = false; break;
                case "ssr" when environment is not null: environment.SsrEnabled = false; break;
                case "glow" when environment is not null: environment.GlowEnabled = false; break;
                case "volfog" when environment is not null: environment.VolumetricFogEnabled = false; break;
                case "fog" when environment is not null: environment.FogEnabled = false; break;
                case "shadows":
                    foreach (var light in all.OfType<DirectionalLight3D>())
                        light.ShadowEnabled = false;
                    break;
                case "msaa": GetViewport().Msaa3D = Viewport.Msaa.Disabled; break;
                case "smaa":
                    GetViewport().Msaa3D = Viewport.Msaa.Disabled;
                    GetViewport().ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Smaa;
                    break;
                case "pcss":
                    foreach (var light in all.OfType<DirectionalLight3D>())
                        light.LightAngularDistance = 0;
                    break;
                default:
                    foreach (var node in all.OfType<Node3D>().Where(n => n.Name == part))
                        node.Visible = false;
                    break;
            }
            GD.Print($"perf: switched off {part}");
        }
    }
}
