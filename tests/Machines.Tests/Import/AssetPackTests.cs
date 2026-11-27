using System.Text;
using Machines.Game;

namespace Machines.Tests.Import;

/// <summary>
/// The asset pack (<see cref="AssetPack"/>): every look's assets sealed in the download under a key only the game's own
/// files give, opened by the import into <c>classic/</c> byte for byte and with their times, and the release check
/// that tells what an installation lacks or has otherwise. The game copies here are made up; their files stand at the
/// paths of <see cref="GameDisc.Known"/>.
/// </summary>
public class AssetPackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"asset-pack-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Folder(string name)
    {
        var folder = Path.Combine(_root, name);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static void Write(string folder, string path, string text, DateTime? time = null)
    {
        var file = Path.Combine(folder, path);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        if (time is { } at)
            File.SetLastWriteTimeUtc(file, at);
    }

    /// <summary>A made-up game copy: the files the key is made of, each with <paramref name="salt"/> in it.</summary>
    private string Copy(string name, string salt = "")
    {
        var copy = Folder(name);
        foreach (var print in GameDisc.Known)
            Write(copy, print.Path.Replace('\\', Path.DirectorySeparatorChar), print.Path + salt);
        return copy;
    }

    /// <summary>A made-up <c>classic/</c>: assets of several looks, a texture with its compressed cache, and what the making leaves.</summary>
    private string Classic()
    {
        var classic = Folder("classic");
        var then = new DateTime(2026, 11, 8, 14, 20, 0, DateTimeKind.Utc);
        Write(classic, "models/red/GRUNT_1.json", "{\"parts\":[]}", then);
        Write(classic, "remake/models/red/GRUNT_1.json", "{\"tyres\":true}", then);
        Write(classic, "hd/models/grunt.png", new string('p', 5000), then);
        Write(classic, "hd/models/grunt.png.dds", new string('d', 3000), then.AddDays(15));
        Write(classic, "meshy/textures/apc-2/albedo.jpg", new string('j', 7000), then);
        Write(classic, "format.txt", "10", then);
        Write(classic, "models.log", "exported", then);
        Write(classic, "game_path.txt", "C:/Users/someone/Machines", then);
        Write(classic, "hd-work/models/x.png", "half", then);
        Write(classic, "hd/models/grunt.png.dds.12", "half written", then);
        return classic;
    }

    private static readonly string[] Assets =
        ["format.txt", "hd/models/grunt.png", "hd/models/grunt.png.dds", "meshy/textures/apc-2/albedo.jpg", "models/red/GRUNT_1.json", "remake/models/red/GRUNT_1.json"];

    [Fact]
    public void The_pack_opens_into_the_same_files_with_their_times()
    {
        var classic = Classic();
        var copy = Copy("copy");
        var pack = Path.Combine(_root, "pack");
        var manifest = AssetPack.Build(classic, pack, AssetPack.Key(copy), (_, _) => { });
        Assert.Equal(Assets, manifest.Files.Select(entry => entry.Path));
        // Sealed: no file of the pack holds its asset's bytes.
        Assert.DoesNotContain("tyres", File.ReadAllText(Path.Combine(pack, "remake", "models", "red", "GRUNT_1.json")));

        var target = Folder("installed");
        Write(target, "remake/models/red/OLD.json", "left from before");
        Write(target, "classic.log", "a log stays");
        AssetPack.Install(pack, target, AssetPack.Key(copy), (_, _) => { });

        foreach (var path in Assets)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(classic, path)), File.ReadAllBytes(Path.Combine(target, path)));
            Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(classic, path)), File.GetLastWriteTimeUtc(Path.Combine(target, path)));
        }
        Assert.False(File.Exists(Path.Combine(target, "remake", "models", "red", "OLD.json")));
        Assert.True(File.Exists(Path.Combine(target, "classic.log")));
        Assert.Equal(manifest.Id, AssetPack.Opened(target));
        Assert.True(AssetPack.Compare(AssetPack.Scan(classic), AssetPack.Scan(target)).Same);
    }

    [Fact]
    public void Another_copy_does_not_open_the_pack()
    {
        var pack = Path.Combine(_root, "pack");
        AssetPack.Build(Classic(), pack, AssetPack.Key(Copy("copy")), (_, _) => { });
        var other = AssetPack.Key(Copy("other", salt: "!"));
        Assert.False(AssetPack.Opens(AssetPack.Read(pack)!, other));
        Assert.Throws<InvalidDataException>(() => AssetPack.Install(pack, Folder("installed"), other, (_, _) => { }));
    }

    [Fact]
    public void A_sealed_file_opens_only_with_its_key_and_at_its_path_and_unchanged()
    {
        var key = AssetPack.Key(Copy("copy"));
        var plain = Encoding.UTF8.GetBytes("the grunt's tyres");
        var sealedBytes = AssetPack.Seal(plain, "remake/models/red/GRUNT_1.json", key);
        Assert.Equal(plain, AssetPack.Open(sealedBytes, "remake/models/red/GRUNT_1.json", key));
        Assert.Throws<InvalidDataException>(() => AssetPack.Open(sealedBytes, "remake/models/red/GRUNT_2.json", key));
        Assert.Throws<InvalidDataException>(() => AssetPack.Open(sealedBytes, "remake/models/red/GRUNT_1.json", AssetPack.Key(Copy("other", "!"))));
        sealedBytes[^1] ^= 1;
        Assert.Throws<InvalidDataException>(() => AssetPack.Open(sealedBytes, "remake/models/red/GRUNT_1.json", key));
    }

    [Fact]
    public void A_file_changed_in_the_pack_stops_the_opening()
    {
        var copy = Copy("copy");
        var pack = Path.Combine(_root, "pack");
        AssetPack.Build(Classic(), pack, AssetPack.Key(copy), (_, _) => { });
        File.WriteAllBytes(Path.Combine(pack, "format.txt"), AssetPack.Seal("11"u8.ToArray(), "format.txt", AssetPack.Key(copy)));
        Assert.Throws<InvalidDataException>(() => AssetPack.Install(pack, Folder("installed"), AssetPack.Key(copy), (_, _) => { }));
    }

    [Fact]
    public void The_key_needs_the_files_not_their_published_fingerprints()
    {
        var one = AssetPack.Key(Copy("copy"));
        Assert.Equal(one, AssetPack.Key(Copy("same")));
        Assert.NotEqual(one, AssetPack.Key(Copy("other", "!")));
        Assert.Equal(32, one.Length);
    }

    [Fact]
    public void Building_again_keeps_what_did_not_change_and_drops_what_went()
    {
        var classic = Classic();
        var key = AssetPack.Key(Copy("copy"));
        var pack = Path.Combine(_root, "pack");
        AssetPack.Build(classic, pack, key, (_, _) => { });
        var kept = File.ReadAllBytes(Path.Combine(pack, "format.txt"));
        File.Delete(Path.Combine(classic, "meshy", "textures", "apc-2", "albedo.jpg"));
        Write(classic, "remake/models/red/GRUNT_1.json", "{\"tyres\":\"ribbed\"}");
        var manifest = AssetPack.Build(classic, pack, key, (_, _) => { });

        Assert.Equal(kept, File.ReadAllBytes(Path.Combine(pack, "format.txt")));
        Assert.False(File.Exists(Path.Combine(pack, "meshy", "textures", "apc-2", "albedo.jpg")));
        var target = Folder("installed");
        AssetPack.Install(pack, target, key, (_, _) => { });
        Assert.Equal("{\"tyres\":\"ribbed\"}", File.ReadAllText(Path.Combine(target, "remake", "models", "red", "GRUNT_1.json")));
        Assert.Equal(5, manifest.Files.Count);
    }

    [Fact]
    public void The_release_check_tells_what_is_missing_different_or_more()
    {
        var classic = Classic();
        var installed = Folder("installed");
        foreach (var path in Assets.Where(path => !path.StartsWith("meshy", StringComparison.Ordinal)))
            File.Copy(Path.Combine(classic, path), Path.Combine(Folder("installed/" + Path.GetDirectoryName(path)), Path.GetFileName(path)));
        Write(installed, "hd/models/grunt.png", "upscaled on another graphics card");
        Write(installed, "hd/models/tank.png.dds", "a cache the game made");

        var differences = AssetPack.Compare(AssetPack.Scan(classic), AssetPack.Scan(installed));
        Assert.Equal(["meshy/textures/apc-2/albedo.jpg"], differences.Missing);
        Assert.Equal(["hd/models/grunt.png"], differences.Differing);
        Assert.Equal(["hd/models/tank.png.dds"], differences.Extra);
        Assert.False(differences.Same);
    }

    [Theory]
    [InlineData("models/red/GRUNT_1.json", true)]
    [InlineData("hd/models/grunt.png.dds", true)]
    [InlineData("format.txt", true)]
    [InlineData("models.log", false)]
    [InlineData("game_path.txt", false)]
    [InlineData("pack.txt", false)]
    [InlineData("hd-work/models/x.png", false)]
    [InlineData("hd/models/grunt.png.dds.12", false)]
    [InlineData("remake/models/red/GRUNT_1.json.7.tmp", false)]
    public void Only_the_assets_belong_to_the_pack(string path, bool belongs)
    {
        Assert.Equal(belongs, AssetPack.Belongs(path));
    }
}
