using Godot;

namespace Machines.Game;

/// <summary>
/// The original's texture animations on an effect (ANIMATION_DATA in its .cdf, written beside it by the exporter and
/// kept on its material by <see cref="ClassicAssets.EffectMesh"/>): a texture running over the mesh, in repeats a
/// second (UV_TRANSLATE), or a cycle of textures, each for so many rendered frames (CYCLE_TEXTURE).
/// </summary>
public static class TextureMotion
{
    private const float FrameRate = 25;

    public static bool Has(BaseMaterial3D material) => material.HasMeta("scroll") || material.HasMeta("frames");

    /// <summary>For a mesh sharing its materials with others of its kind: all on one clock.</summary>
    public static void Show(Mesh mesh, float time)
    {
        for (var s = 0; s < mesh.GetSurfaceCount(); s++)
            if (mesh.SurfaceGetMaterial(s) is BaseMaterial3D material)
                Show(material, time);
    }

    public static void Show(BaseMaterial3D material, float age)
    {
        if (material.HasMeta("scroll"))
        {
            var speed = material.GetMeta("scroll").AsVector2() * age;
            material.Uv1Offset = new Vector3(speed.X, speed.Y, 0);
        }
        if (material.HasMeta("frames"))
        {
            var frames = material.GetMeta("frames").AsGodotArray<Texture2D>();
            material.AlbedoTexture = frames[(int)(age * FrameRate) / material.GetMeta("repeats").AsInt32() % frames.Count];
        }
    }
}
