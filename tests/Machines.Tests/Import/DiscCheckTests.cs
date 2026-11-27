using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Machines.Game;

namespace Machines.Tests.Import;

/// <summary>
/// The check of the player's CD for the complete package (<see cref="GameDisc.Check"/>): CD 1, a folder of what is on
/// it and a copy installed from it pass where their files are the game's, and anything else is turned away. The CDs
/// here are made up, with an InstallShield 5 cabinet written as the game's CD 1 has it, and checked against fingerprints
/// of their own made-up files; the real CDs are checked when MACHINES_DISC_ISO names them.
/// </summary>
public class DiscCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"disc-check-{Guid.NewGuid():N}");

    private static readonly (string Path, byte[] Bytes)[] GameFiles =
    [
        ("models.bin", Bytes(70_000, 1)),
        (@"pdata\m_green.psb", Bytes(30_000, 2)),
        (@"pdata\roller.psb", Bytes(5_000, 3)),
    ];

    private static readonly IReadOnlyList<GameDisc.Fingerprint> Prints =
        GameFiles.Select(file => new GameDisc.Fingerprint(file.Path, file.Bytes.Length, Convert.ToHexString(SHA256.HashData(file.Bytes)))).ToList();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>Bytes that pack a little but not to nothing, the same for the same seed.</summary>
    private static byte[] Bytes(int length, int seed)
    {
        var random = new Random(seed);
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
            bytes[i] = (byte)(i % 7 == 0 ? random.Next(256) : i / 13);
        return bytes;
    }

    private string Place(string name)
    {
        var place = Path.Combine(_root, name);
        Directory.CreateDirectory(place);
        return place;
    }

    private static void Write(string folder, string path, byte[] bytes)
    {
        var file = Path.Combine(folder, path.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, bytes);
    }

    /// <summary>
    /// A folder holding what is on a CD 1: <c>data1.hdr</c> naming <paramref name="files"/> and <c>data1.cab</c> holding
    /// those packed, in pieces of raw deflate each behind its length; those not packed lie on the disc beside it.
    /// </summary>
    private string CdOne(string name, IEnumerable<(string Path, byte[] Bytes, bool Packed)> files)
    {
        var disc = Place(name);
        var entries = files.ToList();
        var folders = entries.Select(file => file.Path.Contains('\\') ? file.Path[..file.Path.LastIndexOf('\\')] : "").Distinct().ToList();

        using var cabinet = new MemoryStream();
        var offsets = new List<(long Offset, long Packed)>();
        foreach (var file in entries)
        {
            if (!file.Packed)
            {
                Write(disc, file.Path, file.Bytes);
                offsets.Add((0, 0));
                continue;
            }
            var start = cabinet.Position;
            for (var at = 0; at < file.Bytes.Length; at += 20_000)
            {
                using var piece = new MemoryStream();
                using (var deflate = new DeflateStream(piece, CompressionLevel.Optimal, leaveOpen: true))
                    deflate.Write(file.Bytes, at, Math.Min(20_000, file.Bytes.Length - at));
                cabinet.Write(BitConverter.GetBytes((ushort)piece.Length));
                cabinet.Write(piece.ToArray());
            }
            offsets.Add((start, cabinet.Position - start));
        }
        File.WriteAllBytes(Path.Combine(disc, "data1.cab"), cabinet.ToArray());

        // The header: its signature and version, the descriptor at 0x200, its table at 0x300, with the folders'
        // names first and then the files' descriptors, each found by its offset from the table.
        var table = new MemoryStream();
        var pointers = new List<int>();
        int Text(string text)
        {
            var at = (int)table.Position;
            table.Write(Encoding.Latin1.GetBytes(text + "\0"));
            return at;
        }
        var slots = folders.Count + entries.Count;
        table.Write(new byte[slots * 4]);
        foreach (var folder in folders)
            pointers.Add(Text(folder));
        for (var i = 0; i < entries.Count; i++)
        {
            var file = entries[i];
            var nameAt = Text(file.Path.Contains('\\') ? file.Path[(file.Path.LastIndexOf('\\') + 1)..] : file.Path);
            var descriptor = new byte[0x2a];
            BitConverter.GetBytes(nameAt).CopyTo(descriptor, 0);
            BitConverter.GetBytes(folders.IndexOf(file.Path.Contains('\\') ? file.Path[..file.Path.LastIndexOf('\\')] : "")).CopyTo(descriptor, 4);
            BitConverter.GetBytes((ushort)(file.Packed ? 4 : 0)).CopyTo(descriptor, 8);
            BitConverter.GetBytes((uint)file.Bytes.Length).CopyTo(descriptor, 10);
            BitConverter.GetBytes((uint)offsets[i].Packed).CopyTo(descriptor, 14);
            BitConverter.GetBytes((uint)offsets[i].Offset).CopyTo(descriptor, 0x26);
            pointers.Add((int)table.Position);
            table.Write(descriptor);
        }
        var tableBytes = table.ToArray();
        for (var i = 0; i < pointers.Count; i++)
            BitConverter.GetBytes(pointers[i]).CopyTo(tableBytes, i * 4);

        var header = new byte[0x300 + tableBytes.Length];
        BitConverter.GetBytes(0x28635349u).CopyTo(header, 0);
        BitConverter.GetBytes(500u).CopyTo(header, 4);
        BitConverter.GetBytes(0x200u).CopyTo(header, 0x0c);
        BitConverter.GetBytes(0x100u).CopyTo(header, 0x200 + 0x0c);
        BitConverter.GetBytes((uint)folders.Count).CopyTo(header, 0x200 + 0x1c);
        BitConverter.GetBytes((uint)entries.Count).CopyTo(header, 0x200 + 0x28);
        tableBytes.CopyTo(header, 0x300);
        File.WriteAllBytes(Path.Combine(disc, "data1.hdr"), header);
        return disc;
    }

    private string CdOne(string name) => CdOne(name, GameFiles.Select(file => (file.Path, file.Bytes, true)).Append(("readme.txt", Bytes(100, 9), true)));

    [Fact]
    public void Cd_one_whose_cabinet_unpacks_to_the_game_files_passes()
    {
        Assert.Equal(GameDisc.Proof.Owned, GameDisc.Check(CdOne("cd1"), Prints));
    }

    [Fact]
    public void A_file_the_cabinet_names_but_leaves_on_the_disc_is_read_from_there()
    {
        var disc = CdOne("loose", GameFiles.Select((file, i) => (file.Path, file.Bytes, i != 1)));
        Assert.Equal(GameDisc.Proof.Owned, GameDisc.Check(disc, Prints));
        File.WriteAllBytes(Path.Combine(disc, "pdata", "m_green.psb"), Bytes(30_000, 4));
        Assert.Equal(GameDisc.Proof.Different, GameDisc.Check(disc, Prints));
    }

    [Fact]
    public void Cd_one_with_other_bytes_in_a_game_file_fails()
    {
        var other = GameFiles.Select(file => (file.Path, file.Path == "models.bin" ? Bytes(70_000, 5) : file.Bytes, true));
        Assert.Equal(GameDisc.Proof.Different, GameDisc.Check(CdOne("forged", other), Prints));
    }

    [Fact]
    public void Cd_one_without_a_game_file_or_with_one_of_another_length_fails()
    {
        Assert.Equal(GameDisc.Proof.Different, GameDisc.Check(CdOne("short", GameFiles.Skip(1).Select(file => (file.Path, file.Bytes, true))), Prints));
        var longer = GameFiles.Select(file => (file.Path, file.Path == @"pdata\roller.psb" ? [.. file.Bytes, 0] : file.Bytes, true));
        Assert.Equal(GameDisc.Proof.Different, GameDisc.Check(CdOne("longer", longer), Prints));
    }

    [Fact]
    public void A_cabinet_that_is_not_one_fails()
    {
        var disc = CdOne("broken");
        File.WriteAllBytes(Path.Combine(disc, "data1.hdr"), Bytes(4_000, 6));
        Assert.Equal(GameDisc.Proof.Different, GameDisc.Check(disc, Prints));
    }

    [Fact]
    public void A_copy_installed_from_the_cd_passes_and_one_with_other_files_fails()
    {
        var copy = Place("installed");
        foreach (var (path, bytes) in GameFiles)
            Write(copy, path, bytes);
        Assert.Equal(GameDisc.Proof.Owned, GameDisc.Check(copy, Prints));
        Write(copy, @"pdata\roller.psb", Bytes(5_000, 7));
        Assert.Equal(GameDisc.Proof.Different, GameDisc.Check(copy, Prints));
    }

    [Fact]
    public void Cd_two_is_told_apart_and_does_not_pass()
    {
        var disc = Place("cd2");
        Write(disc, @"flics\podland.smk", Bytes(1_000, 8));
        Assert.Equal(GameDisc.Proof.SecondCd, GameDisc.Check(disc, Prints));
    }

    [Fact]
    public void Anything_else_is_no_proof()
    {
        var folder = Place("empty");
        Write(folder, "notes.txt", Bytes(10, 1));
        Assert.Equal(GameDisc.Proof.None, GameDisc.Check(folder, Prints));
        Assert.Equal(GameDisc.Proof.None, GameDisc.Check(Path.Combine(folder, "notes.txt"), Prints));
        Write(folder, "fake.iso", Bytes(100_000, 2));
        Assert.Equal(GameDisc.Proof.None, GameDisc.Check(Path.Combine(folder, "fake.iso"), Prints));
        Assert.Equal(GameDisc.Proof.None, GameDisc.Check(Path.Combine(_root, "missing"), Prints));
    }

    [Fact]
    public void The_known_fingerprints_name_the_models_and_terrain_once_each()
    {
        Assert.Contains(GameDisc.Known, print => print.Path == "models.bin");
        Assert.Equal(GameDisc.Known.Count, GameDisc.Known.Select(print => print.Path.ToLowerInvariant()).Distinct().Count());
        Assert.All(GameDisc.Known, print =>
        {
            Assert.True(print.Size > 0);
            Assert.Equal(64, print.Sha256.Length);
        });
    }

    /// <summary>
    /// The real CDs, opt-in: MACHINES_DISC_ISO=&lt;CD 1's image&gt;[;&lt;CD 2's image&gt;] and MACHINES_DISC_COPY=&lt;a copy
    /// installed by the original's setup&gt;. CD 1 and the installed copy pass with the known fingerprints, CD 2 is told
    /// apart, and nothing is written beside them.
    /// </summary>
    [Fact]
    public void The_real_cds_pass_the_check()
    {
        var images = Environment.GetEnvironmentVariable("MACHINES_DISC_ISO")?.Split(';');
        if (images is null)
            return;
        Assert.Equal(GameDisc.Proof.Owned, GameDisc.Check(images[0]));
        if (images.Length > 1)
            Assert.Equal(GameDisc.Proof.SecondCd, GameDisc.Check(images[1]));
        if (Environment.GetEnvironmentVariable("MACHINES_DISC_COPY") is { } installed)
            Assert.Equal(GameDisc.Proof.Owned, GameDisc.Check(installed));
    }
}
