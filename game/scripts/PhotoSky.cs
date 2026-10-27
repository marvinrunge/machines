using System.Runtime.InteropServices;
using Godot;

namespace Machines.Game;

/// <summary>
/// The remake's sky over the planets whose original sky is a day sky: photographed skies from Poly Haven (CC0,
/// fetched by tools/terrain-textures/fetch.sh into textures/polyhaven/sky), one for each height of the sun, blended
/// as it climbs and sinks, and tinted to the colours the original's own sky dome had at that hour (its picture times
/// its colour bands). The photographed sun is taken out of each picture, since the planet's sun stands elsewhere; a
/// soft glow is drawn round the real one. The clouds drift slowly. At night the planet's stars come out. The sky also
/// lights the scene a little (reflections and part of the ambient light). Alien skies (the moons, Gracko, the dark
/// forts, the ice planets) keep their painted pictures.
/// </summary>
public sealed class PhotoSky
{
    /// <summary>A picture, the sun's height (degrees) it was taken at, and its file name under textures/polyhaven/sky.</summary>
    private static readonly (float Elevation, string Id)[] PartlyCloudy =
    [
        (-6, "qwantani_night_puresky"), (2, "qwantani_sunset_puresky"), (15, "qwantani_late_afternoon_puresky"),
        (38, "kloofendal_38d_partly_cloudy_puresky"), (48, "kloofendal_48d_partly_cloudy_puresky"),
    ];

    private static readonly (float Elevation, string Id)[] Overcast =
    [
        (-6, "qwantani_night_puresky"), (2, "qwantani_sunset_puresky"), (14, "kloofendal_overcast_puresky"),
    ];

    /// <summary>Which planets take a photographed sky, and which: those whose original sky is blue with clouds, or grey.</summary>
    private static readonly Dictionary<string, (float Elevation, string Id)[]> Planets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m_desert"] = PartlyCloudy, ["desspy"] = PartlyCloudy, ["daynight"] = PartlyCloudy, ["desert"] = PartlyCloudy,
        ["hilly"] = PartlyCloudy, ["talonth8"] = PartlyCloudy, ["finger"] = PartlyCloudy, ["5o1"] = PartlyCloudy,
        ["fortmdn"] = PartlyCloudy, ["fortsdn"] = PartlyCloudy, ["home"] = PartlyCloudy, ["joust"] = PartlyCloudy, ["roller"] = PartlyCloudy,
        ["m_green"] = Overcast, ["mgreendn"] = Overcast, ["m_dales"] = Overcast, ["mdalesdn"] = Overcast,
    };

    /// <summary>How fast the clouds drift round the sky, radians a second of game time (about 2° a minute).</summary>
    private const float Drift = 0.0006f;

    /// <summary>How far round the photographed sun is taken out (degrees), and the glow drawn round the real one.</summary>
    private const float SunReach = 18;

    /// <summary>How much of the scene's ambient light comes from the sky (the rest is the original's colour tables).</summary>
    public const float AmbientShare = 0.35f;

    /// <summary>A picture ready to show, and its average colours (linear) along the horizon and overhead.</summary>
    private sealed record Picture(Texture2D Texture, Vector3 Horizon, Vector3 Top);

    private static readonly Dictionary<string, Picture?> Pictures = [];

    private static string Folder => Path.Combine(Paths.Root, "textures", "polyhaven", "sky");

    private readonly (float Elevation, Picture Picture)[] _set;
    private readonly Vector3 _domeHorizon, _domeTop;
    private int _shownA = -1, _shownB = -1;

    public ShaderMaterial Material { get; } = new() { Shader = SkyShader.Value };

    private PhotoSky((float, Picture)[] set, Vector3 domeHorizon, Vector3 domeTop)
    {
        (_set, _domeHorizon, _domeTop) = (set, domeHorizon, domeTop);
    }

    /// <summary>
    /// The photographed sky for a planet whose sun climbs between the given heights, tinted to its original dome
    /// picture; null for planets that keep their own sky, or when the pictures have not been fetched.
    /// </summary>
    public static PhotoSky? For(string planet, float lowest, float highest, Texture2D? dome)
    {
        if (!Planets.TryGetValue(planet, out var set) || dome?.GetImage() is not { } domeImage)
            return null;
        // Only the pictures the sun's heights reach, and the one on either side to blend with.
        var first = Math.Max(0, Array.FindLastIndex(set, s => s.Elevation <= lowest));
        var last = Array.FindIndex(set, s => s.Elevation >= highest) is var found and >= 0 ? found : set.Length - 1;
        var needed = set[first..(last + 1)];
        var pictures = new Picture?[needed.Length];
        Parallel.For(0, needed.Length, i => pictures[i] = Load(needed[i].Id));
        if (pictures.Any(p => p is null))
            return null;
        var (domeHorizon, domeTop) = DomeColours(domeImage);
        return new PhotoSky([.. needed.Select((s, i) => (s.Elevation, pictures[i]!))], domeHorizon, domeTop);
    }

    /// <summary>
    /// Shows the sky for the sun's height: the two pictures either side of it, blended, each tinted so that its
    /// horizon and overhead take the colours the original's dome has now (its picture's colours times the bands').
    /// </summary>
    public void Update(float elevation, Color horizonBand, Color topBand, Vector3 towardsSun, Color sunColour, bool sunDisc,
        float stars, float time)
    {
        var b = Array.FindIndex(_set, s => s.Elevation > elevation);
        var (ia, ib) = b < 0 ? (_set.Length - 1, _set.Length - 1) : b == 0 ? (0, 0) : (b - 1, b);
        var blend = ia == ib ? 0 : Mathf.SmoothStep(0, 1, (elevation - _set[ia].Elevation) / (_set[ib].Elevation - _set[ia].Elevation));
        if (ia != _shownA || ib != _shownB)
        {
            (_shownA, _shownB) = (ia, ib);
            Material.SetShaderParameter("sky_a", _set[ia].Picture.Texture);
            Material.SetShaderParameter("sky_b", _set[ib].Picture.Texture);
        }
        // What the original's dome shows now, in linear light.
        var horizon = Linear(new Vector3(_domeHorizon.X * horizonBand.R, _domeHorizon.Y * horizonBand.G, _domeHorizon.Z * horizonBand.B));
        var top = Linear(new Vector3(_domeTop.X * topBand.R, _domeTop.Y * topBand.G, _domeTop.Z * topBand.B));
        Material.SetShaderParameter("blend", blend);
        Material.SetShaderParameter("horizon_gain_a", Gain(horizon, _set[ia].Picture.Horizon));
        Material.SetShaderParameter("top_gain_a", Gain(top, _set[ia].Picture.Top));
        Material.SetShaderParameter("horizon_gain_b", Gain(horizon, _set[ib].Picture.Horizon));
        Material.SetShaderParameter("top_gain_b", Gain(top, _set[ib].Picture.Top));
        Material.SetShaderParameter("turn", time * Drift);
        Material.SetShaderParameter("sun_direction", towardsSun);
        Material.SetShaderParameter("sun_colour", new Vector3(sunColour.R, sunColour.G, sunColour.B) * (elevation > -2 ? 1 : 0));
        Material.SetShaderParameter("sun_disc", sunDisc ? 1f : 0f);
        Material.SetShaderParameter("stars_alpha", stars);
    }

    public void SetStars(Texture2D stars) => Material.SetShaderParameter("stars", stars);

    public void SetGround(Color ground) => Material.SetShaderParameter("ground", new Vector3(ground.R, ground.G, ground.B));

    private static Vector3 Gain(Vector3 target, Vector3 picture) =>
        new Vector3(target.X / Mathf.Max(picture.X, 1e-4f), target.Y / Mathf.Max(picture.Y, 1e-4f), target.Z / Mathf.Max(picture.Z, 1e-4f)).Clamp(Vector3.One * 0.005f, Vector3.One * 12);

    private static Vector3 Linear(Vector3 srgb)
    {
        static float L(float s) => s <= 0.04045f ? s / 12.92f : Mathf.Pow((s + 0.055f) / 1.055f, 2.4f);
        return new Vector3(L(Mathf.Clamp(srgb.X, 0, 1)), L(Mathf.Clamp(srgb.Y, 0, 1)), L(Mathf.Clamp(srgb.Z, 0, 1)));
    }

    /// <summary>The original dome picture's average colours (as stored, sRGB): its sky just above the horizon, and its top.</summary>
    private static (Vector3 Horizon, Vector3 Top) DomeColours(Image source)
    {
        var image = (Image)source.Duplicate();
        if (image.IsCompressed())
            image.Decompress();
        image.ClearMipmaps();
        image.Convert(Image.Format.Rgb8);
        image.Resize(32, 32, Image.Interpolation.Bilinear);
        Vector3 Average(int fromRow, int toRow)
        {
            var sum = Vector3.Zero;
            for (var y = fromRow; y < toRow; y++)
                for (var x = 0; x < 32; x++)
                {
                    var c = image.GetPixel(x, y);
                    sum += new Vector3(c.R, c.G, c.B);
                }
            return sum / ((toRow - fromRow) * 32);
        }
        // The pictures put the horizon about two thirds down (hills and ground below it): the sky just above it, and the top.
        return (Average(13, 18), Average(0, 9));
    }

    /// <summary>
    /// A picture from its HDR file, its photographed sun taken out (and the brightest of its glow held down), with its
    /// average colours along the horizon (0–10° up) and overhead (50° and higher). Loaded once.
    /// </summary>
    private static Picture? Load(string id)
    {
        lock (Pictures)
            if (Pictures.TryGetValue(id, out var cached))
                return cached;
        var path = Path.Combine(Folder, id + ".hdr");
        Picture? picture = null;
        if (File.Exists(path) && Image.LoadFromFile(path) is { } image && !image.IsEmpty())
        {
            image.Convert(Image.Format.Rgbf);
            var (width, height) = (image.GetWidth(), image.GetHeight());
            var bytes = image.GetData();
            var pixels = MemoryMarshal.Cast<byte, float>(bytes.AsSpan()).ToArray();
            TakeOutSun(pixels, width, height);
            var horizon = Average(pixels, width, height, 0, 10);
            var top = Average(pixels, width, height, 50, 90);
            MemoryMarshal.Cast<float, byte>(pixels.AsSpan()).CopyTo(bytes);
            var shown = Image.CreateFromData(width, height, false, Image.Format.Rgbf, bytes);
            shown.Convert(Image.Format.Rgbe9995);
            picture = new Picture(ImageTexture.CreateFromImage(shown), horizon, top);
        }
        lock (Pictures)
            Pictures[id] = picture;
        return picture;
    }

    /// <summary>The direction a pixel of an equirectangular picture looks (y up), as the sky shader reads them.</summary>
    private static Vector3 Direction(float x, float y, int width, int height)
    {
        var azimuth = (x / width - 0.5f) * Mathf.Tau;
        var polar = y / height * Mathf.Pi;
        return new Vector3(Mathf.Sin(polar) * Mathf.Sin(azimuth), Mathf.Cos(polar), -Mathf.Sin(polar) * Mathf.Cos(azimuth));
    }

    private static float Luminance(float[] p, int i) => 0.2126f * p[i] + 0.7152f * p[i + 1] + 0.0722f * p[i + 2];

    /// <summary>
    /// Finds the photographed sun (the brightest point, if far brighter than the sky) and holds everything within
    /// <see cref="SunReach"/> of it to the brightness of the sky around, a little more towards the edge.
    /// </summary>
    private static void TakeOutSun(float[] pixels, int width, int height)
    {
        var (brightest, at, sum) = (0f, 0, 0.0);
        for (var y = 0; y < height / 2; y++)
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 3;
                var l = Luminance(pixels, i);
                sum += l;
                if (l > brightest)
                    (brightest, at) = (l, y * width + x);
            }
        var average = (float)(sum / (width * height / 2));
        if (brightest < average * 25)
            return;
        var sun = Direction(at % width + 0.5f, at / width + 0.5f, width, height);
        var reach = Mathf.Cos(Mathf.DegToRad(SunReach));
        var ringFrom = Mathf.Cos(Mathf.DegToRad(SunReach + 4));
        // Only rows near the sun's height can lie within reach of it.
        var sunRow = at / width;
        var rows = (int)Mathf.Ceil((SunReach + 4) / 180f * height) + 1;
        var (top, bottom) = (Math.Max(0, sunRow - rows), Math.Min(height, sunRow + rows + 1));
        // The sky in a ring round the reach: how bright it is.
        var (ring, count) = (0.0, 0);
        for (var y = top; y < bottom; y++)
            for (var x = 0; x < width; x++)
            {
                var c = Direction(x + 0.5f, y + 0.5f, width, height).Dot(sun);
                if (c < reach && c >= ringFrom)
                {
                    ring += Luminance(pixels, (y * width + x) * 3);
                    count++;
                }
            }
        var ringLight = count > 0 ? (float)(ring / count) : average;
        Parallel.For(top, bottom, y =>
        {
            for (var x = 0; x < width; x++)
            {
                var c = Direction(x + 0.5f, y + 0.5f, width, height).Dot(sun);
                if (c < reach)
                    continue;
                var angle = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(c, -1, 1)));
                var cap = ringLight * Mathf.Lerp(1.05f, 1.6f, angle / SunReach);
                var i = (y * width + x) * 3;
                var l = Luminance(pixels, i);
                if (l > cap)
                {
                    var scale = cap / l;
                    (pixels[i], pixels[i + 1], pixels[i + 2]) = (pixels[i] * scale, pixels[i + 1] * scale, pixels[i + 2] * scale);
                }
            }
        });
    }

    /// <summary>The average colour of the picture between two heights above the horizon (degrees).</summary>
    private static Vector3 Average(float[] pixels, int width, int height, float lowest, float highest)
    {
        var (from, to) = ((int)(height * (0.5f - highest / 180)), (int)(height * (0.5f - lowest / 180)));
        var sum = Vector3.Zero;
        var count = 0;
        for (var y = Math.Max(0, from); y < Math.Min(height, to); y++)
            for (var x = 0; x < width; x += 4)
            {
                var i = (y * width + x) * 3;
                sum += new Vector3(pixels[i], pixels[i + 1], pixels[i + 2]);
                count++;
            }
        return count > 0 ? sum / count : Vector3.One;
    }

    /// <summary>
    /// The two pictures blended and tinted (towards the overhead tint as the view rises), turned by the drift; a glow
    /// round the sun, its disc when it has no picture of its own, stars at night, and the ground's colour below the
    /// horizon. The pictures' directions match <see cref="Direction"/>.
    /// </summary>
    private static readonly Lazy<Shader> SkyShader = new(() => new Shader
    {
        Code = """
            shader_type sky;
            uniform sampler2D sky_a : filter_linear, repeat_enable;
            uniform sampler2D sky_b : filter_linear, repeat_enable;
            uniform float blend = 0.0;
            uniform vec3 horizon_gain_a = vec3(1.0);
            uniform vec3 top_gain_a = vec3(1.0);
            uniform vec3 horizon_gain_b = vec3(1.0);
            uniform vec3 top_gain_b = vec3(1.0);
            uniform float turn = 0.0;
            uniform vec3 sun_direction = vec3(0.0, 1.0, 0.0);
            uniform vec3 sun_colour = vec3(1.0);
            uniform float sun_disc = 0.0;
            uniform sampler2D stars : source_color, filter_linear;
            uniform float stars_alpha = 0.0;
            uniform vec3 ground = vec3(0.3);

            void sky() {
                vec3 d = EYEDIR;
                float polar = acos(clamp(d.y, -1.0, 1.0)) / PI;
                float azimuth = atan(d.x, -d.z);
                vec2 uv = vec2(fract((azimuth + turn) / TAU + 0.5), polar);
                float up = smoothstep(0.08, 0.8, d.y);
                vec3 a = texture(sky_a, uv).rgb * mix(horizon_gain_a, top_gain_a, up);
                vec3 b = texture(sky_b, uv).rgb * mix(horizon_gain_b, top_gain_b, up);
                vec3 colour = mix(a, b, blend);
                // The real sun: a soft glow round it, and its disc where it has no picture of its own.
                float c = max(dot(d, normalize(sun_direction)), 0.0);
                colour += sun_colour * (0.5 * pow(c, 48.0) + 0.15 * pow(c, 6.0));
                colour += sun_colour * sun_disc * smoothstep(0.99955, 0.9998, c) * 24.0;
                colour += texture(stars, vec2(fract(azimuth / TAU + 0.5), polar)).rgb * stars_alpha * smoothstep(0.0, 0.12, d.y);
                COLOR = mix(colour, ground, smoothstep(0.02, -0.12, d.y));
            }
            """,
    });
}
