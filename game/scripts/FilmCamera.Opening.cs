using Godot;

namespace Machines.Game;

// An opening in a deck's floor that the stars show through (the script's "opening" cue): the shaft under pod 3 as it is
// let go, the pod falling away into space below the ship.
public abstract partial class FilmCamera
{
    /// <summary>The layer the cutscene's props are also drawn on, the only one the opening's view below shows.</summary>
    private const uint OpeningLayer = 1u << 19;

    private SubViewport? _opening;
    private Camera3D? _below;
    private MeshInstance3D? _hole;
    private Callable? _beforeDraw;

    /// <summary>
    /// Opens a round hole <paramref name="radius"/> metres across in the floor at <paramref name="at"/>, lying
    /// <paramref name="above"/> metres over it (over the cradle's base): it is drawn as a window onto a second view, taken
    /// each frame from where the camera stands, that shows nothing of the deck, only the space stage's sky and the
    /// cutscene's props. What falls through the floor is seen falling on below it, among the stars. Its rim darkens, as a
    /// shaft's walls would.
    /// </summary>
    private void Opening(Vector3 at, float radius, float above)
    {
        if (_opening is not null || GetParent() is not Node3D parent || GetViewport() is not { } main)
            return;
        _opening = new SubViewport
        {
            Name = "OpeningView",
            World3D = main.FindWorld3D(),
            Size = (Vector2I)main.GetVisibleRect().Size,
            Msaa3D = main.Msaa3D,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        _below = new Camera3D { Name = "Below", CullMask = OpeningLayer, Environment = SpaceStage.Sky(), Far = 20_000 };
        _opening.AddChild(_below);
        AddChild(_opening);
        _below.MakeCurrent();
        _hole = new MeshInstance3D
        {
            Name = "Opening",
            Mesh = new PlaneMesh { Size = Vector2.One * 2 },
            MaterialOverride = new ShaderMaterial { Shader = HoleShader.Value },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Layers = 1,
        };
        ((ShaderMaterial)_hole.MaterialOverride).SetShaderParameter("view", _opening.GetTexture());
        parent.AddChild(_hole);
        _hole.GlobalTransform = new Transform3D(Basis.FromScale(new Vector3(radius, 1, radius)), at + Vector3.Up * above);
        foreach (var prop in _props.Values)
            SeenBelow(prop.Node);
        // The view below moves with the camera, set just before the frame is drawn so the two never part.
        _beforeDraw = Callable.From(FollowBelow);
        RenderingServer.Singleton.Connect(RenderingServerInstance.SignalName.FramePreDraw, _beforeDraw.Value);
    }

    // The view below where the camera is, as it looks.
    private void FollowBelow()
    {
        if (_below is null || _opening is null || !IsInstanceValid(_below) || GetViewport() is not { } main || main.GetCamera3D() is not { } camera)
            return;
        _below.GlobalTransform = camera.GlobalTransform;
        _below.Fov = camera.Fov;
        _below.Near = camera.Near;
        _opening.Size = (Vector2I)main.GetVisibleRect().Size;
    }

    /// <summary>For the warm-up: an opening's disc as it is drawn (with no view below yet), to be shown once.</summary>
    public static MeshInstance3D OpeningSample() => new()
    {
        Name = "WarmOpening",
        Mesh = new PlaneMesh { Size = Vector2.One * 2 },
        MaterialOverride = new ShaderMaterial { Shader = HoleShader.Value },
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
    };

    /// <summary>A prop drawn in the view below the opening as well.</summary>
    private static void SeenBelow(Node node)
    {
        if (node is VisualInstance3D visual)
            visual.Layers |= OpeningLayer;
        foreach (var child in node.GetChildren())
            SeenBelow(child);
    }

    /// <summary>The opening closed (the cutscene's end).</summary>
    private void CloseOpening()
    {
        if (_beforeDraw is { } callable && RenderingServer.Singleton.IsConnected(RenderingServerInstance.SignalName.FramePreDraw, callable))
            RenderingServer.Singleton.Disconnect(RenderingServerInstance.SignalName.FramePreDraw, callable);
        _beforeDraw = null;
        if (_hole is not null && IsInstanceValid(_hole))
            _hole.QueueFree();
        if (_opening is not null && IsInstanceValid(_opening))
            _opening.QueueFree();
        (_hole, _opening, _below) = (null, null, null);
    }

    private static readonly Lazy<Shader> HoleShader = new(() => new Shader
    {
        Code = """
                shader_type spatial;
                render_mode unshaded, cull_disabled, shadows_disabled, fog_disabled;

                uniform sampler2D view : source_color, filter_linear;

                void fragment() {
                    float r = length(UV * 2.0 - 1.0);
                    if (r > 1.0) discard;
                    // The shaft's wall seen at its edge: dark, a thin cold glint where the floor ends.
                    float wall = smoothstep(0.78, 0.98, r);
                    float lip = exp(-pow((r - 0.985) * 90.0, 2.0));
                    ALBEDO = texture(view, SCREEN_UV).rgb * (1.0 - wall * 0.92) + vec3(0.55, 0.6, 0.65) * lip * 0.35;
                }
                """,
    });
}
