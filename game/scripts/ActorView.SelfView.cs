using Godot;

namespace Machines.Game;

// The driven machine seen from its own faceplate (user's wish after the first play test): first person used to leave the
// whole machine out of its camera (DrivenLayer). Now only the parts the camera sits in or right beside stay out, so looking
// down shows its legs, wheels or tracks and its guns, and nothing is ever cut open by the near plane. Each frame, for each mesh:
// the camera's distance to the mesh's own box, in the mesh's space so a long thin leg is measured as it stands.
public partial class ActorView
{
    /// <summary>How close the camera may come to a part's box before the part is left out, metres (the near plane is 0.1).</summary>
    private const float SelfClearance = 0.35f;

    /// <summary>
    /// Shows the driven machine to the first-person camera at <paramref name="camera"/>, all but the parts within
    /// <see cref="SelfClearance"/> of it; null leaves the whole machine out again (from behind, in a film).
    /// </summary>
    public void ShowSelf(Vector3? camera)
    {
        if (!_driven || _model is null)
            return;
        // The model only: its marker, shadow plate, lamp cones and effects stay out of the picture.
        if (_model is MeshInstance3D whole)
            whole.Layers = camera is { } at && Clear(whole, at) ? 1u : DrivenLayer;
        Layer(_model, camera);
    }

    private static void Layer(Node node, Vector3? camera)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is MeshInstance3D mesh)
                mesh.Layers = camera is { } at && mesh.Visible && Clear(mesh, at) ? 1u : DrivenLayer;
            Layer(child, camera);
        }
    }

    /// <summary>Whether a part stands clear of the camera: its box, grown by the clearance, does not hold it.</summary>
    private static bool Clear(MeshInstance3D mesh, Vector3 camera)
    {
        var box = mesh.GetAabb();
        if (box.Size == Vector3.Zero)
            return false;
        var scale = mesh.GlobalBasis.Scale;
        var grow = SelfClearance / Mathf.Max(0.001f, Mathf.Min(scale.X, Mathf.Min(scale.Y, scale.Z)));
        return !box.Grow(grow).HasPoint(mesh.GlobalTransform.AffineInverse() * camera);
    }
}
