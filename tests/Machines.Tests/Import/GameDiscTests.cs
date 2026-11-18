using Machines.Game;

namespace Machines.Tests.Import;

public class GameDiscTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"disc-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Folder(params string[] files)
    {
        Directory.CreateDirectory(_root);
        foreach (var file in files)
        {
            var path = Path.Combine(_root, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, file);
        }
        return _root;
    }

    [Fact]
    public void A_folder_with_the_cabinet_is_cd_one_whatever_the_case_of_its_names()
    {
        using var disc = new FolderDisc(Folder("DATA1.HDR", "Data1.cab"));
        Assert.Equal(GameDisc.Kind.One, GameDisc.Which(disc));
        Assert.True(disc.Has("data1.hdr"));
    }

    [Fact]
    public void A_folder_with_the_campaign_films_and_no_cabinet_is_cd_two()
    {
        using var disc = new FolderDisc(Folder(@"flics\Podland.smk", @"flics\Outro.smk", @"flics\gui\x.smk"));
        Assert.Equal(GameDisc.Kind.Two, GameDisc.Which(disc));
        Assert.Equal([@"flics\gui\x.smk", @"flics\Outro.smk", @"flics\Podland.smk"],disc.Files("flics").Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void An_installed_copy_is_no_disc()
    {
        Assert.Null(GameDisc.Open(Folder("models.bin", @"pdata\x.psb")));
    }

    [Fact]
    public void A_file_that_is_no_disc_image_is_no_disc()
    {
        Assert.Null(GameDisc.Open(Path.Combine(Folder("broken.iso"), "broken.iso")));
    }

    /// <summary>
    /// The real CDs, opt-in: MACHINES_DISC_ISO=&lt;CD 1's image&gt;[;&lt;CD 2's image&gt;] and MACHINES_DISC_COPY=&lt;a copy
    /// installed by the original's setup&gt;. CD 1 unpacks into a copy the import takes, every file that the installed
    /// copy has too is the same to the byte, and CD 2 adds the campaign's films.
    /// </summary>
    [Fact]
    public void The_cds_unpack_into_the_copy_the_original_setup_installs()
    {
        var images = Environment.GetEnvironmentVariable("MACHINES_DISC_ISO")?.Split(';');
        var installed = Environment.GetEnvironmentVariable("MACHINES_DISC_COPY");
        if (images is null || installed is null)
            return;

        using (var one = GameDisc.Open(images[0]))
        {
            Assert.NotNull(one);
            Assert.Equal(GameDisc.Kind.One, GameDisc.Which(one));
            GameDisc.Install(one, _root, (_, _) => { });
        }
        Assert.True(GameLocator.Holds(_root));
        if (images.Length > 1)
        {
            using var two = GameDisc.Open(images[1]);
            Assert.NotNull(two);
            Assert.Equal(GameDisc.Kind.Two, GameDisc.Which(two));
            GameDisc.AddFilms(two, _root, (_, _) => { });
            Assert.True(GameDisc.HasFilms(_root));
        }

        var compared = 0;
        var differing = new List<string>();
        foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(_root, file);
            var other = Path.Combine(installed, relative);
            if (!File.Exists(other))
                continue;
            compared++;
            if (!File.ReadAllBytes(file).AsSpan().SequenceEqual(File.ReadAllBytes(other)))
                differing.Add(relative);
        }
        Assert.Empty(differing);
        Assert.True(compared > 7000, $"only {compared} files to compare");
    }
}
