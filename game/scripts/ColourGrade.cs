using Godot;

namespace Machines.Game;

/// <summary>
/// The remake's colour grade for each kind of planet, as a lookup table the environment applies after tone mapping
/// (Environment.AdjustmentColorCorrection): the shade takes a colour of its own and the light another, a little apart,
/// as a film's grade sets them. The green planets keep warm light over cool green shade, the desert golden light over
/// blue shade, the ice cold blue shade under clean white, the ash worlds embers' orange over crimson, the moons a
/// paler, cooler grey. Stations and industrial worlds stay as lit. Gentle by design: the planet's own colours lead.
/// </summary>
public static class ColourGrade
{
    /// <summary>How the shade and the light are tinted (added in the picture's colours, 0 to 1) and how saturated.</summary>
    private readonly record struct Grade(Vector3 Shade, Vector3 Light, float Saturation);

    private static readonly Dictionary<Weather.Kind, Grade> Grades = new()
    {
        [Weather.Kind.Green] = new(new(-0.012f, 0.006f, 0.022f), new(0.024f, 0.012f, -0.022f), 1.04f),
        [Weather.Kind.Desert] = new(new(-0.016f, 0.0f, 0.032f), new(0.03f, 0.012f, -0.03f), 1.03f),
        [Weather.Kind.Ice] = new(new(-0.022f, 0.004f, 0.04f), new(-0.004f, 0.0f, 0.01f), 0.97f),
        [Weather.Kind.Ash] = new(new(0.028f, -0.01f, -0.006f), new(0.034f, 0.008f, -0.03f), 1.05f),
        [Weather.Kind.Moon] = new(new(-0.008f, 0.0f, 0.016f), Vector3.Zero, 0.9f),
    };

    private static readonly Dictionary<Weather.Kind, Texture3D> Tables = [];

    /// <summary>Size of the table along each colour.</summary>
    private const int Size = 33;

    /// <summary>The table for <paramref name="planet"/>, or null where the light stays as it is.</summary>
    public static Texture3D? For(string planet)
    {
        if (Weather.KindOf(planet) is not { } kind || !Grades.TryGetValue(kind, out var grade))
            return null;
        if (!Tables.TryGetValue(kind, out var table))
            Tables[kind] = table = Build(grade);
        return table;
    }

    /// <summary>
    /// The table: red along its width, green its height, blue its depth, each from 0 to 1 at its first and last texel
    /// (the environment looks a colour up there as it is).
    /// </summary>
    private static ImageTexture3D Build(Grade grade)
    {
        var slices = new Godot.Collections.Array<Image>();
        for (var b = 0; b < Size; b++)
        {
            var slice = Image.CreateEmpty(Size, Size, false, Image.Format.Rgb8);
            for (var g = 0; g < Size; g++)
                for (var r = 0; r < Size; r++)
                {
                    var c = Apply(grade, new Vector3(r, g, b) / (Size - 1));
                    slice.SetPixel(r, g, new Color(c.X, c.Y, c.Z));
                }
            slices.Add(slice);
        }
        var texture = new ImageTexture3D();
        texture.Create(Image.Format.Rgb8, Size, Size, Size, false, slices);
        return texture;
    }

    /// <summary>One colour graded: the shade's tint fades out towards the middle tones, the light's in from them.</summary>
    private static Vector3 Apply(Grade grade, Vector3 colour)
    {
        var light = colour.Dot(new Vector3(0.2126f, 0.7152f, 0.0722f));
        var shade = 1 - Smooth(0.05f, 0.55f, light);
        var lit = Smooth(0.4f, 0.95f, light);
        var graded = colour + grade.Shade * shade + grade.Light * lit;
        var grey = graded.Dot(new Vector3(0.2126f, 0.7152f, 0.0722f));
        graded = new Vector3(grey, grey, grey).Lerp(graded, grade.Saturation);
        // Black stays black and white white: the tints fade at the very ends.
        var ends = Smooth(0.0f, 0.04f, light) * (1 - Smooth(0.97f, 1.0f, light));
        graded = colour.Lerp(graded, ends);
        return graded.Clamp(Vector3.Zero, Vector3.One);
    }

    private static float Smooth(float from, float to, float x)
    {
        var t = Mathf.Clamp((x - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
