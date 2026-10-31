using Godot;

namespace Machines.Game;

public partial class ActorView
{
    // Whether the marker is the 2D selection box rather than the wireframe box, and the box's material.
    private bool _marker2d;
    private ShaderMaterial? _marker2dMaterial;

    // The 2D selection box faces the camera and is drawn in its colour, fading out close to the camera as the
    // wireframe does. It lies where the model's middle is, but meets the depth test `front` metres nearer the
    // camera, so the model does not hide it (RenTTFPolygon's depth offset).
    private static readonly Shader Marker2dShader = new()
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;
            uniform vec4 colour : source_color = vec4(0.0, 0.76, 0.0, 0.7);
            uniform float front = 0.0;
            varying float view_depth;
            void vertex() {
                MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3])
                    * mat4(vec4(length(MODEL_MATRIX[0].xyz), 0.0, 0.0, 0.0), vec4(0.0, length(MODEL_MATRIX[1].xyz), 0.0, 0.0),
                        vec4(0.0, 0.0, length(MODEL_MATRIX[2].xyz), 0.0), vec4(0.0, 0.0, 0.0, 1.0));
                vec4 view = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
                vec4 clip = PROJECTION_MATRIX * view;
                vec4 nearer = PROJECTION_MATRIX * vec4(view.xy, min(view.z + front, -0.1), 1.0);
                POSITION = vec4(clip.xy, nearer.z / nearer.w * clip.w, clip.w);
                view_depth = -view.z;
            }
            void fragment() {
                ALBEDO = colour.rgb;
                ALPHA = colour.a * smoothstep(3.0, 10.0, view_depth);
            }
            """,
    };

    /// <summary>
    /// Builds the marker the options ask for (MachPhysMarker): the wireframe box round the model or, with the options'
    /// 2D Selection box, the corners of a frame facing the camera, a little see-through.
    /// </summary>
    private void BuildMarker()
    {
        _marker2d = GameOptions.Selection2d;
        if (!_marker2d)
        {
            _marker.Mesh = MarkerBox(_bounds);
            _marker.Position = Vector3.Zero;
            _marker.MaterialOverride = _markerMaterial;
            return;
        }
        _marker.Mesh = MarkerCorners(_bounds, out var centre, out var front);
        _marker.Position = centre;
        _marker2dMaterial ??= new ShaderMaterial { Shader = Marker2dShader };
        _marker2dMaterial.SetShaderParameter("front", front);
        _marker.MaterialOverride = _marker2dMaterial;
    }

    /// <summary>
    /// The 2D selection box (MachPhysMarker with the 2D cursor type): four corners, each two lines along the frame's
    /// edges cut off by the corner's diagonal, as the original drew its corner bitmap (mark3_t.bmp) on four triangles.
    /// The frame is as wide as the model's mean width, at least 3 m, and as high as the model, at least 2.5 m and
    /// a little higher for a flat one. Larger ones have thicker lines (0.25 to 0.8 m), smaller ones longer corners
    /// (12/32 to 7/32 of the frame). For the depth test it stands 0.6 of its width in front of the model's middle.
    /// </summary>
    private static ArrayMesh MarkerCorners(Aabb bounds, out Vector3 centre, out float front)
    {
        var (width, depth) = (bounds.Size.X, bounds.Size.Z);
        var height = bounds.End.Y - Mathf.Max(bounds.Position.Y, 0);
        var mean = (width + depth) / 2;
        var frameWidth = Mathf.Max(mean, 3);
        if (width > height)
            height = (3 * height + width) / 4;
        var frameHeight = Mathf.Max(height, 2.5f);
        // Made higher, the frame rises so its foot stays out of the ground.
        centre = bounds.GetCenter() + Vector3.Up * Mathf.Max(frameHeight - height, 0) / 2;
        front = 0.6f * frameWidth;

        var thickness = Mathf.Remap(Mathf.Clamp(frameWidth, 3.5f, 15), 3.5f, 15, 0.25f, 0.8f);
        var corner = Mathf.Remap(Mathf.Clamp(mean, 5, 15), 5, 15, 12 / 32f, 7 / 32f);
        var (cw, ch, t) = (corner * frameWidth, corner * frameHeight, thickness);

        var mesh = new ArrayMesh();
        var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        foreach (var (sx, sy) in new[] { (-1, 1), (1, 1), (-1, -1), (1, -1) })
        {
            // a runs in from the corner along the frame's top or bottom, b along its side.
            Vector3 At(float a, float b) => new(sx * (frameWidth / 2 - a), sy * (frameHeight / 2 - b), 0);
            void Quad(Vector3 p, Vector3 q, Vector3 r, Vector3 s)
            {
                foreach (var v in new[] { p, q, r, p, r, s })
                    tool.AddVertex(v);
            }
            Quad(At(0, 0), At(cw, 0), At(cw * (1 - t / ch), t), At(0, t));
            Quad(At(0, t), At(t, t), At(t, ch * (1 - t / cw)), At(0, ch));
        }
        tool.Commit(mesh);
        return mesh;
    }
}
