using Godot;

namespace Machines.Game;

/// <summary>
/// Something blowing up as the original showed it, for the classic look: fireballs of the original's animated
/// pictures (MachPhysFireball), a machine's parts flying off spinning (MachPhysMachineExplosion), a building
/// shrinking and swelling (MachPhysObjectExplosion) and breaking into pieces (MachPhysObjDemolish), the wave running out over the ground (MachPhysShockWave,
/// MachPhysBurstWave), a flash of light and a building's smoke rising for a long while (MachPhysSmokePlume).
/// Every piece is a step of time; the node frees itself when the last is over.
/// </summary>
public partial class Blowup : Node3D
{
    // MachPhysFireballImpl::timePerFrame: twenty pictures a second.
    private const float FrameTime = 0.05f;

    // A machine's shock wave (NUCLEAR_MISSILE's EXTRAS in parmdata.dat): from a tenth of its smaller width to
    // four times its larger one in half a second, at least 2 m to start with.
    private const float ShockFrom = 0.1f, ShockTo = 4, ShockTime = 0.5f, ShockMinFrom = 2;

    private const int MaxLights = 6;
    private static int _lights;

    private readonly List<Func<float, bool>> _parts = [];
    private float _age;

    public override void _Process(double delta)
    {
        _age += (float)delta;
        _parts.RemoveAll(part => !part(_age));
        if (_parts.Count == 0)
            QueueFree();
    }

    private static float Random(float from, float to) => from + (float)System.Random.Shared.NextDouble() * (to - from);

    /// <summary>
    /// A machine goes up (MachPhysMachineExplosion::explodeMachine): a fireball four and a half to six times its
    /// size covers it, every part flies off from the bottom of its box, spinning the faster the lighter it is, and
    /// bursts into a fireball of its own after 0.3 to 0.6 s; a shock wave runs out with a flash of light.
    /// </summary>
    public static void Machine(ActorView view, Node3D? model, ClassicAssets assets, Node parent)
    {
        var blowup = new Blowup { Name = "Blowup" };
        parent.AddChild(blowup);
        var frames = Frames(assets);
        var place = view.GlobalTransform;
        var box = view.Bounds;
        var size = box.Size.Length();
        const float offset = 0.05f;

        var jitter = box.Size * new Vector3(Random(-0.2f, 0.2f), Random(-0.2f, 0.2f), Random(-0.2f, 0.2f));
        blowup.Fireball(frames, place * (box.GetCenter() + jitter), Random(4.5f, 6) * size, offset, size / 2,
            Random(-Mathf.Pi / 8, 7 * Mathf.Pi / 8));

        // The parts fly from the middle of the bottom of the machine's box (MachPhysMachineData::explosionCenter).
        var centre = place * new Vector3(box.GetCenter().X, box.Position.Y, box.GetCenter().Z);
        if (model is not null)
            blowup.ShootOff(model, centre, frames, offset);

        var (narrow, wide) = (Mathf.Min(box.Size.X, box.Size.Z), Mathf.Max(box.Size.X, box.Size.Z));
        var (from, to) = (narrow * ShockFrom, wide * ShockTo);
        if (from < ShockMinFrom)
            (from, to) = (ShockMinFrom, ShockMinFrom * to / Mathf.Max(from, 0.01f));
        blowup.Wave(assets, "shockwave", view.GlobalPosition, from, to, offset, ShockTime, flat: 0.1f);
        blowup.Light(place * box.GetCenter(), new Color(1, 0.65f, 0.17f), 2.3f, (from + to) / 2, offset, ShockTime);
    }

    /// <summary>
    /// The parts of <paramref name="model"/> fly off (MachPhysMachineExplosion::setupMotionPlans): straight away
    /// from <paramref name="centre"/> at seven times their distance from it a second, turning about a random axis
    /// at 100 to 200 radians a second for the smallest part, slower by the root of how much heavier (bigger cubed)
    /// a part is; each vanishes in a fireball two to three times its size.
    /// </summary>
    private void ShootOff(Node3D model, Vector3 centre, Texture2D[] frames, float offset)
    {
        // A machine without parts is one mesh itself.
        var parts = (model is MeshInstance3D whole ? [whole] : Enumerable.Empty<MeshInstance3D>())
            .Concat(model.FindChildren("*", nameof(MeshInstance3D), recursive: true, owned: false).OfType<MeshInstance3D>())
            .Where(m => m.Mesh is not null && m.IsVisibleInTree())
            .Select(m => (Mesh: m, Size: (m.GlobalBasis * m.Mesh!.GetAabb().Size).Length()))
            .ToList();
        if (parts.Count == 0)
            return;
        var smallest = parts.Where(p => p.Size > 0.01f).Select(p => p.Size).DefaultIfEmpty(1).Min();
        var largest = parts.Max(p => p.Size);
        foreach (var (mesh, size) in parts)
        {
            var piece = new MeshInstance3D { Mesh = mesh.Mesh, MaterialOverride = mesh.MaterialOverride, Layers = 1 };
            AddChild(piece);
            var start = mesh.GlobalTransform;
            piece.GlobalTransform = start;
            var middle = start * mesh.Mesh!.GetAabb().GetCenter();
            var away = (middle - centre) * new Vector3(Random(0.9f, 1.1f), Random(0.9f, 1.1f), Random(0.9f, 1.1f));
            var velocity = away * 7 * Random(0.7f, 1.3f);
            var weight = Mathf.Max(1, Mathf.Pow(Mathf.Max(size, 0.01f) / smallest, 3));
            var rate = Mathf.Max(0.5f, Random(100, 200) / Mathf.Sqrt(weight));
            var axis = new Vector3(Random(-1, 1), Random(-1, 1), Random(-1, 1));
            axis = axis.LengthSquared() > 0.0001f ? axis.Normalized() : Vector3.Up;
            var gone = offset + Random(0.3f, 0.6f);
            _parts.Add(age =>
            {
                var t = Mathf.Max(0, age - offset);
                piece.GlobalTransform = new Transform3D(new Basis(axis, rate * t) * start.Basis, start.Origin + velocity * t);
                if (age < gone)
                    return true;
                piece.QueueFree();
                return false;
            });
            // The fireball goes off where the part's centre has got to, in front of everything (depth offset of
            // the largest part's size).
            Fireball(frames, middle + velocity * (gone - offset), Random(2, 3) * size, gone, largest, Random(0, Mathf.Tau));
        }
    }

    /// <summary>
    /// A building goes up (MachPhysObjectExplosion::explode): a burst wave from where it shrinks to, a fireball four
    /// to six times its size, more fireballs over a big one, a flash of light, and a few puffs of smoke rising
    /// to its size for half a minute. The building itself shrinks and swells (see <see cref="Swell"/>).
    /// </summary>
    public static void Building(ActorView view, ClassicAssets assets, Node parent)
    {
        var blowup = new Blowup { Name = "Blowup" };
        parent.AddChild(blowup);
        var frames = Frames(assets);
        var place = view.GlobalTransform;
        var box = view.Bounds;
        var (sizeX, sizeZ, top) = (box.Size.X, box.Size.Z, box.End.Y);
        var dimension = Mathf.Max(Mathf.Max(sizeX, sizeZ), box.Size.Y);
        const float start = 0.1f;

        // From where the building has shrunk to, out to twice what it swells to, in a second.
        var (from, to) = (Mathf.Min(sizeX, sizeZ) * 0.6f, Mathf.Max(sizeX, sizeZ) * 1.8f * 2);
        if (from < 7)
            (from, to) = (7, 7 * to / Mathf.Max(from, 0.01f));
        blowup.Wave(assets, "burstwave", view.GlobalPosition, from, to, start, 1, flat: 1);

        var size = box.Size.Length();
        var middle = new Vector3(box.GetCenter().X, 0, box.GetCenter().Z);
        blowup.Fireball(frames, place * middle, Random(4, 6) * size, start, size / 2, Random(0, Mathf.Pi));
        blowup.Light(place * (middle + Vector3.Up * 2), new Color(1, 0.67f, 0.17f), 3, 1.8f * dimension, start, 0.3f);

        var (reachX, reachZ) = (0.4f * sizeX, 0.4f * sizeZ);
        for (var i = 1; i <= (int)(dimension / 9); i++)
        {
            var ball = Random(Mathf.Min(reachX, reachZ), Mathf.Max(reachX, reachZ));
            var at = new Vector3(box.GetCenter().X + Random(-sizeX / 2, sizeX / 2), Mathf.Max(ball / 2, Random(0, top)),
                box.GetCenter().Z + Random(-sizeZ / 2, sizeZ / 2));
            blowup.Fireball(frames, place * at, ball, Random(0.3f, 0.6f), Random(0, Mathf.Min(reachX, reachZ)), Random(0, Mathf.Pi));
        }

        blowup.Plume(assets, place * middle, 1 + (int)(dimension / 15), Mathf.Min(sizeX, sizeZ), dimension);
    }

    /// <summary>
    /// A building blowing up shrinks to 60 % in a tenth of a second, swells to 180 % in 0.15 s and is gone
    /// (MachPhysObjectExplosion). <paramref name="model"/> is moved out of its view for it; in the remake's
    /// effects it glows red-hot as it swells (the original meant it to glow white, but its plan is commented out).
    /// </summary>
    public static void Swell(Node3D view, Node3D model, Node parent, bool glow)
    {
        var holder = new Node3D { Name = "Swelling" };
        parent.AddChild(holder);
        holder.GlobalTransform = view.GlobalTransform;
        model.Reparent(holder);
        var tween = holder.CreateTween();
        tween.TweenProperty(holder, "scale", Vector3.One * 0.6f, 0.1f);
        tween.TweenProperty(holder, "scale", Vector3.One * 1.8f, 0.15f);
        tween.TweenCallback(Callable.From(holder.QueueFree));
        if (!glow)
            return;
        var heat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            AlbedoColor = Colors.Black,
        };
        foreach (var mesh in model.FindChildren("*", nameof(MeshInstance3D), recursive: true, owned: false).OfType<MeshInstance3D>())
            mesh.MaterialOverlay = heat;
        holder.CreateTween().TweenProperty(heat, "albedo_color", new Color(1.6f, 0.55f, 0.12f), 0.2f).SetDelay(0.05f);
    }

    /// <summary>
    /// A building breaks into pieces as it swells (MachPhysObjDemolish::startDemolition), shown from 0.15 s to 0.6 s
    /// of its blowing up: they fly apart as their cycle moves them, unlit, glowing white to black over 0.22 s and
    /// then fading out black. A building without pieces of its own (all but the level 1 mine) gets the generic ones,
    /// made for the military factory and scaled to its size. In the remake's effects they glow red-hot instead.
    /// </summary>
    public static void Demolish(ActorView view, string id, ClassicAssets assets, Node parent, bool hot)
    {
        var own = id.Length > 0 ? assets.EffectRig("demolish/" + id) : null;
        if ((own ?? assets.EffectRig("demolish/generic")) is not { } rig || !rig.Animations.TryGetValue("demolish", out var cycle))
            return;
        const float start = 0.15f, glowing = 0.22f, fading = 0.23f;
        // MachPhysObjectExplosion::explode: the generic pieces scale by the building's largest size over the
        // military factory's, 39.58 m.
        var box = view.Bounds;
        var scale = own is not null ? 1 : Mathf.Max(Mathf.Max(box.Size.X, box.Size.Z), box.Size.Y) / 39.58f;
        var blowup = new Blowup { Name = "Demolition" };
        parent.AddChild(blowup);
        var holder = new Node3D { Visible = false };
        blowup.AddChild(holder);
        holder.GlobalTransform = view.GlobalTransform.ScaledLocal(Vector3.One * scale);

        var heat = hot ? new Color(2, 1.1f, 0.45f) : Colors.White;
        var material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Always,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            AlbedoColor = heat,
        };
        var pieces = rig.Nodes.Select(node => node.Mesh is { } mesh
                ? new MeshInstance3D { Mesh = mesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off }
                : new Node3D())
            .ToArray();
        for (var i = 0; i < pieces.Length; i++)
        {
            pieces[i].Transform = rig.Nodes[i].Rest;
            (rig.Nodes[i].Parent >= 0 ? pieces[rig.Nodes[i].Parent] : holder).AddChild(pieces[i]);
        }
        blowup._parts.Add(age =>
        {
            var t = age - start;
            if (t < 0)
                return true;
            if (t >= glowing + fading)
            {
                holder.QueueFree();
                return false;
            }
            holder.Visible = true;
            // The cycle is a little shorter than the pieces are shown; they stay where it ends.
            foreach (var track in cycle.Tracks)
                if (track.Node < pieces.Length)
                    pieces[track.Node].Transform = track.At(t / cycle.Duration);
            material.AlbedoColor = t < glowing ? heat.Lerp(Colors.Black, t / glowing) : Colors.Black with { A = 1 - (t - glowing) / fading };
            return true;
        });
    }

    /// <summary>
    /// An energy weapon's hit, a third of a second long: its splat playing its pictures as it grows to twice its
    /// size, the light sting's to four times (MachPhysPlasmaSplat, MachPhysPulseSplat, MachPhysLightStingSplat),
    /// and the flare of its aura swelling to twice its size, back and down to half (MachPhysPlasmaAura,
    /// MachPhysPulseAura, MachPhysLightStingAura). Other weapons leave none.
    /// </summary>
    public static void Hit(string weapon, Vector3 at, ClassicAssets assets, Node parent)
    {
        const float duration = 0.33f;
        // The pictures, and the sizes of splat and aura as the exported models have them.
        var (pictures, splatSize, auraSize, grow) =
            weapon.Contains("PLASMA") ? (Enumerable.Range(0, 4).Select(i => $"spl{i:D2}_bt.bmp"), 4f, 8f, 2f)
            : weapon.Contains("PULSE") ? (Enumerable.Range(0, 4).Select(i => $"pls{i:D2}_bt.bmp"), 8f, 8f, 2f)
            : weapon == "WASP_LIGHT_STING" ? (Enumerable.Range(0, 3).Select(i => $"bhit{i}_bt.bmp"), 4f, 4f, 4f)
            : (Enumerable.Empty<string>(), 0f, 0f, 0f);
        var frames = pictures.Select(p => assets.Texture("models/texture4/" + p)).OfType<Texture2D>().ToArray();
        if (frames.Length == 0)
            return;
        var blowup = new Blowup { Name = "Hit" };
        parent.AddChild(blowup);
        // Drawn as much nearer as they grow across, so that neither the machine hit nor the ground cuts them.
        blowup.Picture(frames, at, splatSize, 0, duration, splatSize * grow, 0, scale: t => Mathf.Lerp(1, grow, t));
        if (assets.Texture("models/texture4/flar2_bt.bmp") is { } flare)
            blowup.Picture([flare], at, auraSize, 0, duration, auraSize * 2, 0,
                scale: t => t < 1 / 3f ? 1 + t * 3 : t < 2 / 3f ? 2 - (t * 3 - 1) : 1 - (t * 3 - 2) / 2);
    }

    /// <summary>
    /// A pulse weapon fires a round: a blue ring at the muzzle across the line of fire grows from 1 to 5 m round
    /// in a third of a second, fading from half strength (MachPhysPulseWeapon::createPulseDisc).
    /// </summary>
    public static void PulseDisc(Vector3 at, Vector3 direction, ClassicAssets assets, Node parent)
    {
        if (assets.EffectMesh("disc") is not { } mesh || direction.LengthSquared() < 0.0001f)
            return;
        const float duration = 0.33f;
        var material = mesh.SurfaceGetMaterial(0) is StandardMaterial3D shared ? (StandardMaterial3D)shared.Duplicate() : new StandardMaterial3D();
        material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        material.EmissionEnabled = false;
        var blue = new Color(95 / 255f, 174 / 255f, 1);
        material.AlbedoColor = blue with { A = 0.5f };
        // The disc lies across its x axis, which goes along the line of fire.
        var along = direction.Normalized();
        var side = Mathf.Abs(along.Y) < 0.99f ? Vector3.Up.Cross(along).Normalized() : Vector3.Right;
        var basis = new Basis(along, side.Cross(along), side);
        var disc = new MeshInstance3D { Mesh = mesh, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        parent.AddChild(disc);
        disc.GlobalTransform = new Transform3D(basis, at);
        var grow = disc.CreateTween().SetParallel();
        grow.TweenMethod(Callable.From<float>(s => disc.GlobalTransform = new Transform3D(basis.Scaled(Vector3.One * s), at)), 1f, 5f, duration);
        grow.TweenProperty(material, "albedo_color:a", 0f, duration).SetEase(Tween.EaseType.In).SetTrans(Tween.TransitionType.Quad);
        grow.Chain().TweenCallback(Callable.From(disc.QueueFree));
    }

    // ---- pieces ---------------------------------------------------------------------------------

    /// <summary>The fireball's pictures (MachPhysFireballImpl::materials1): the bang, then the ball of fire.</summary>
    private static Texture2D[] Frames(ClassicAssets assets) =>
        Enumerable.Range(0, 10).Select(i => $"models/texture4/bng{i:D2}_bt.bmp")
            // The list goes on to ex27, but the game has only up to ex16.
            .Concat(Enumerable.Range(0, 28).Select(i => $"models/texture4/ex{i:D2}_bt.bmp"))
            .Select(assets.Texture).OfType<Texture2D>().ToArray();

    /// <summary>
    /// A fireball (MachPhysFireball): a picture <paramref name="size"/> metres across facing the camera, turned by
    /// <paramref name="turn"/>, playing its frames from <paramref name="start"/> seconds; drawn as if
    /// <paramref name="toward"/> metres nearer the camera so that it is not cut by what blows up.
    /// </summary>
    private void Fireball(Texture2D[] frames, Vector3 at, float size, float start, float toward, float turn) =>
        // The original's offset leaves the lower half in the ground under a camera looking down; a whole size
        // brings all of it in front. The last pictures the game has are of the full ball; it fades over them
        // rather than vanishing.
        Picture(frames, at, size, start, frames.Length * FrameTime, Mathf.Max(toward, size), turn,
            alpha: t => Mathf.Clamp((1 - t) * frames.Length / 4, 0, 1));

    /// <summary>
    /// A picture <paramref name="size"/> metres across facing the camera (W4dSprite3d), turned by
    /// <paramref name="turn"/>, playing <paramref name="frames"/> evenly over <paramref name="duration"/> seconds from
    /// <paramref name="start"/> (W4dMaterialSequencePlan); drawn as if <paramref name="toward"/> metres nearer the
    /// camera; its <paramref name="scale"/> and <paramref name="alpha"/> by the share of its time gone.
    /// </summary>
    private void Picture(Texture2D[] frames, Vector3 at, float size, float start, float duration, float toward, float turn,
        Func<float, float>? scale = null, Func<float, float>? alpha = null)
    {
        if (frames.Length == 0 || size <= 0)
            return;
        var material = new ShaderMaterial { Shader = SpriteShader.Value };
        material.SetShaderParameter("turn", turn);
        material.SetShaderParameter("toward", toward);
        var sprite = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(size, size) },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(sprite);
        sprite.GlobalPosition = at;
        var shown = -1;
        _parts.Add(age =>
        {
            var t = (age - start) / duration;
            if (t < 0)
                return true;
            if (t >= 1)
            {
                sprite.QueueFree();
                return false;
            }
            var frame = Math.Min((int)(t * frames.Length), frames.Length - 1);
            if (frame != shown)
            {
                material.SetShaderParameter("picture", frames[frame]);
                shown = frame;
            }
            sprite.Visible = true;
            if (scale is not null)
                sprite.Scale = Vector3.One * Mathf.Max(0.01f, scale(t));
            if (alpha is not null)
                material.SetShaderParameter("alpha", alpha(t));
            return true;
        });
    }

    /// <summary>
    /// The wave running out over the ground (MachPhysShockWave, MachPhysBurstWave): its <paramref name="model"/>,
    /// 2 m across and 2 m high, stretched from <paramref name="from"/> to <paramref name="to"/> metres across over
    /// <paramref name="duration"/> seconds and <paramref name="flat"/> of its height, fading from a quarter of the
    /// way as 1 - 2x + x².
    /// </summary>
    private void Wave(ClassicAssets assets, string model, Vector3 at, float from, float to, float start, float duration, float flat)
    {
        if (assets.EffectMesh(model) is not { } mesh)
            return;
        var material = mesh.SurfaceGetMaterial(0) is StandardMaterial3D shared ? (StandardMaterial3D)shared.Duplicate() : new StandardMaterial3D();
        material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
        material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        material.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        if (material.EmissionEnabled)
        {
            material.AlbedoColor = new Color(material.Emission, material.AlbedoColor.A);
            material.EmissionEnabled = false;
        }
        var clear = material.AlbedoColor.A;
        var wave = new MeshInstance3D
        {
            Mesh = mesh,
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false,
        };
        AddChild(wave);
        _parts.Add(age =>
        {
            var t = (age - start) / duration;
            if (t < 0)
                return true;
            if (t >= 1)
            {
                wave.QueueFree();
                return false;
            }
            wave.Visible = true;
            var reach = Mathf.Lerp(from, to, t) / 2;
            wave.GlobalTransform = new Transform3D(Basis.FromScale(new Vector3(reach, flat, reach)), at);
            // Alpha 1 until 4/16, then 9/16, 4/16, 1/16 and 0 at 7/16, 10/16, 13/16 and the end.
            var fade = t < 0.25f ? 1 : Mathf.Pow(Mathf.Max(0, 1 - (t - 0.25f) / 0.75f), 2);
            material.AlbedoColor = material.AlbedoColor with { A = clear * fade };
            return true;
        });
    }

    /// <summary>A flash of light from <paramref name="start"/>, dying away over <paramref name="duration"/> (W4dUniformLight); a few at a time.</summary>
    private void Light(Vector3 at, Color colour, float energy, float range, float start, float duration)
    {
        if (_lights >= MaxLights)
            return;
        _lights++;
        // The original's light dies away with the square of the distance (attenuation 0, 0.2, 0.79).
        var light = new OmniLight3D { LightColor = colour, LightEnergy = 0, OmniRange = range, OmniAttenuation = 2, ShadowEnabled = false };
        light.TreeExiting += () => _lights--;
        AddChild(light);
        light.GlobalPosition = at;
        _parts.Add(age =>
        {
            var t = (age - start) / duration;
            if (t < 1)
            {
                light.LightEnergy = t < 0 ? 0 : energy * (1 - t);
                return true;
            }
            light.QueueFree();
            return false;
        });
    }

    /// <summary>
    /// The smoke over a ruin (MachPhysSmokePlume, 60 s, started 10 s before so that it is already there):
    /// <paramref name="puffs"/> puffs of the original's smoke about <paramref name="size"/> metres across, each
    /// rising <paramref name="height"/> metres over 30 s on a slightly wandering path, swelling and shrinking again.
    /// </summary>
    private void Plume(ClassicAssets assets, Vector3 at, int puffs, float size, float height)
    {
        const float rise = 30, duration = 60, before = 10;
        string[] pictures = ["models/texture4/smoke/smk03_bt.bmp", "models/texture4/smoke/smk02_bt.bmp"];
        var picture = assets.Texture(pictures[System.Random.Shared.Next(pictures.Length)]);
        if (picture is null)
            return;
        // The plume's path: four legs, each a quarter of the height higher and up to a tenth of it to the side.
        var path = new Vector3[5];
        for (var i = 1; i < path.Length; i++)
        {
            var up = i * height / 4;
            path[i] = new Vector3(Random(-up / 10, up / 10), up, Random(-up / 10, up / 10));
        }
        for (var i = 0; i < puffs; i++)
        {
            var begin = Random(0, duration - rise) - before;
            var puffSize = Random(size * 0.9f, size * 1.3f);
            var material = new ShaderMaterial { Shader = SpriteShader.Value };
            material.SetShaderParameter("picture", picture);
            material.SetShaderParameter("turn", Random(0, Mathf.Tau));
            material.SetShaderParameter("toward", puffSize / 2);
            var puff = new MeshInstance3D
            {
                Mesh = new QuadMesh { Size = new Vector2(puffSize, puffSize) },
                MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Visible = false,
            };
            AddChild(puff);
            _parts.Add(age =>
            {
                var t = (age - begin) / rise;
                if (t < 0)
                    return true;
                if (t >= 1)
                {
                    puff.QueueFree();
                    return false;
                }
                puff.Visible = true;
                var leg = Mathf.Min((int)(t * 4), 3);
                puff.GlobalPosition = at + path[leg].Lerp(path[leg + 1], t * 4 - leg);
                puff.Scale = Vector3.One * Mathf.Max(0.01f, t < 0.5f ? t * 2 : (1 - t) * 2);
                return true;
            });
        }
    }

    /// <summary>
    /// The original's turn-to-face pictures (W4dSprite3d): unlit, see-through by the picture's alpha, turned in
    /// their plane, and drawn at the depth of a point <c>toward</c> metres nearer the camera (depthOffset).
    /// </summary>
    private static readonly Lazy<Shader> SpriteShader = new(() => new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, cull_disabled, depth_draw_never, shadows_disabled;
            uniform sampler2D picture : source_color, filter_linear_mipmap, repeat_disable;
            uniform float turn = 0.0;
            uniform float toward = 0.0;
            uniform float alpha = 1.0;
            void vertex() {
                float c = cos(turn), s = sin(turn);
                vec2 corner = mat2(vec2(c, s), vec2(-s, c)) * VERTEX.xy * length(MODEL_MATRIX[0].xyz);
                vec3 at = (MODELVIEW_MATRIX * vec4(0.0, 0.0, 0.0, 1.0)).xyz + vec3(corner, 0.0);
                vec4 clip = PROJECTION_MATRIX * vec4(at, 1.0);
                vec3 nearer = at + normalize(-at) * clamp(toward, 0.0, max(0.0, length(at) - 1.0));
                vec4 near_clip = PROJECTION_MATRIX * vec4(nearer, 1.0);
                POSITION = vec4(clip.xy, near_clip.z / near_clip.w * clip.w, clip.w);
            }
            void fragment() {
                vec4 c = texture(picture, UV);
                ALBEDO = c.rgb;
                ALPHA = c.a * alpha;
            }
            """,
    });
}
