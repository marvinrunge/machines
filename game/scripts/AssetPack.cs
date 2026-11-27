using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Machines.Game;

/// <summary>
/// The remake's assets of every look, made from the original's (the exported models and terrain, the overhauled and HD
/// models, the HD textures with their compressed caches, the Wired machines, the HD films and the minimaps: all of
/// <c>classic/</c>), carried in the download locked, so that every installation has them byte for byte as they were
/// made, and none of them is of use without the game. Each file is sealed with AES-256-GCM under a key that only the
/// game's own files give (<see cref="Key"/>: <c>models.bin</c> and two planets' terrain, as CD 1 packs them and its
/// setup installs them), with its path bound in. The import unpacks the CD as before and then opens the pack into
/// <c>classic/</c> (<see cref="Install"/>) instead of exporting and upscaling. The original's files themselves are not
/// in the pack. <see cref="Scan"/> and <see cref="Compare"/> are the release check: they list every asset file with
/// its length, time and SHA-256, and tell what an installation lacks or has otherwise than the one it was made from.
/// Nothing here needs Godot, so the tests and <c>tools/asset-pack</c> run it.
/// </summary>
public static class AssetPack
{
    /// <summary>The pack's folder beside the game, and its list of files in it.</summary>
    public const string FolderName = "pack";

    public const string ManifestName = "manifest.json";

    /// <summary>The file in <c>classic/</c> that names the pack it was opened from (<see cref="Manifest.Id"/>).</summary>
    public const string OpenedName = "pack.txt";

    private static readonly byte[] Magic = "MPK1"u8.ToArray();
    private const int NonceSize = 12, TagSize = 16;

    /// <summary>One file of the assets: its path from <c>classic/</c> with forward slashes, length, SHA-256 and time.</summary>
    public sealed record Entry(string Path, long Size, string Sha256, long Time);

    /// <summary>
    /// The pack's list: its files, <see cref="Check"/> (a MAC of the key, so a wrong key is told at once) and
    /// <see cref="Id"/>, which changes with any file.
    /// </summary>
    public sealed record Manifest(int Version, string Check, string Id, IReadOnlyList<Entry> Files);

    /// <summary>What one set of assets lacks, has otherwise, or has more of than another (<see cref="Compare"/>).</summary>
    public sealed record Differences(IReadOnlyList<string> Missing, IReadOnlyList<string> Differing, IReadOnlyList<string> Extra)
    {
        /// <summary>Nothing is missing or different (more files may be there: caches the game made).</summary>
        public bool Same => Missing.Count == 0 && Differing.Count == 0;
    }

    /// <summary>
    /// The key, from the game's own files in <paramref name="copy"/> (a copy unpacked from CD 1 or installed by its
    /// setup): SHA-256 over a label and the files' bytes. It is not their published fingerprints
    /// (<see cref="GameDisc.Known"/>), so it cannot be made without the files.
    /// </summary>
    public static byte[] Key(string copy)
    {
        using var disc = new FolderDisc(copy);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("Machines Remake asset pack 1\0"u8);
        foreach (var print in GameDisc.Known)
        {
            using var file = disc.Open(print.Path);
            hash.AppendData(BitConverter.GetBytes(file.Length));
            var buffer = new byte[1 << 16];
            int read;
            while ((read = file.Read(buffer)) > 0)
                hash.AppendData(buffer, 0, read);
        }
        return hash.GetHashAndReset();
    }

    /// <summary>The files of the assets: none of the logs, work folders and half-written files the making leaves, nor the copy's path.</summary>
    public static bool Belongs(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return !(name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || name.Equals("game_path.txt", StringComparison.OrdinalIgnoreCase)
            || name.Equals(OpenedName, StringComparison.OrdinalIgnoreCase)
            || (path.Split('/') is var parts && parts.Contains("hd-work", StringComparer.OrdinalIgnoreCase))
            // GameDisc writes a texture's compressed cache as <file>.dds.<thread> first and then moves it.
            || (System.IO.Path.GetExtension(name) is { Length: > 1 } number && number[1..].All(char.IsDigit) && name.Contains(".dds.", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Every file in <paramref name="root"/> that <see cref="Belongs"/>, with its length, SHA-256 and time.</summary>
    public static IReadOnlyList<Entry> Scan(string root, Func<string, bool>? include = null)
    {
        if (!Directory.Exists(root))
            return [];
        var paths = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => System.IO.Path.GetRelativePath(root, file).Replace('\\', '/'))
            .Where(path => Belongs(path) && (include?.Invoke(path) ?? true))
            .Order(StringComparer.Ordinal)
            .ToList();
        var entries = new Entry[paths.Count];
        Parallel.For(0, paths.Count, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            var file = System.IO.Path.Combine(root, paths[i]);
            using var stream = File.OpenRead(file);
            entries[i] = new Entry(paths[i], stream.Length, Convert.ToHexString(SHA256.HashData(stream)), Time(file));
        });
        return entries;
    }

    /// <summary>
    /// What <paramref name="have"/> lacks of <paramref name="want"/>, has with other bytes, and has besides; times are
    /// not compared (an installation keeps the pack's, a checkout its own).
    /// </summary>
    public static Differences Compare(IReadOnlyList<Entry> want, IReadOnlyList<Entry> have)
    {
        var there = have.ToDictionary(entry => entry.Path, StringComparer.OrdinalIgnoreCase);
        var wanted = want.Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new Differences(
            want.Where(entry => !there.ContainsKey(entry.Path)).Select(entry => entry.Path).ToList(),
            want.Where(entry => there.TryGetValue(entry.Path, out var other) && (other.Size != entry.Size || !other.Sha256.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)))
                .Select(entry => entry.Path).ToList(),
            have.Where(entry => !wanted.Contains(entry.Path)).Select(entry => entry.Path).ToList());
    }

    /// <summary>
    /// Seals the assets in <paramref name="source"/> (a <c>classic/</c> folder) into <paramref name="pack"/> under
    /// <paramref name="key"/>, one file each at its own path, with the manifest beside them. A file sealed before with
    /// the same bytes and key is kept as it is, so a pack is brought up to date in the time its changes take; files no
    /// longer in the assets are removed.
    /// </summary>
    public static Manifest Build(string source, string pack, byte[] key, Action<int, int> progress, Func<string, bool>? include = null)
    {
        var entries = Scan(source, include);
        var before = Read(pack) is { } old && old.Check == Check(key) ? old.Files.ToDictionary(entry => entry.Path) : [];
        var done = 0;
        Each(entries, new ParallelOptions { MaxDegreeOfParallelism = 4 }, entry =>
        {
            var sealedFile = System.IO.Path.Combine(pack, entry.Path);
            if (!(before.TryGetValue(entry.Path, out var old) && old.Sha256 == entry.Sha256 && File.Exists(sealedFile)))
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(sealedFile)!);
                Write(sealedFile, Seal(File.ReadAllBytes(System.IO.Path.Combine(source, entry.Path)), entry.Path, key));
            }
            var count = Interlocked.Increment(ref done);
            if (count % 100 == 0 || count == entries.Count)
                progress(count, entries.Count);
        });
        var kept = entries.Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.Exists(pack) ? Directory.GetFiles(pack, "*", SearchOption.AllDirectories) : [])
        {
            var relative = System.IO.Path.GetRelativePath(pack, path).Replace('\\', '/');
            if (relative != ManifestName && !kept.Contains(relative))
                File.Delete(path);
        }
        var manifest = new Manifest(1, Check(key), Id(entries), entries);
        Directory.CreateDirectory(pack);
        File.WriteAllText(System.IO.Path.Combine(pack, ManifestName), JsonSerializer.Serialize(manifest, Json));
        return manifest;
    }

    /// <summary>The pack's manifest in <paramref name="pack"/>, or null where there is none.</summary>
    public static Manifest? Read(string pack)
    {
        var file = System.IO.Path.Combine(pack, ManifestName);
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<Manifest>(File.ReadAllText(file), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The key opens the pack: its <see cref="Manifest.Check"/> is the key's.</summary>
    public static bool Opens(Manifest manifest, byte[] key) => manifest.Check == Check(key);

    /// <summary>
    /// Opens the pack in <paramref name="pack"/> into <paramref name="target"/> (<c>classic/</c>): every file unsealed,
    /// checked against its SHA-256 and given the time it was made with (the compressed texture caches are only read
    /// while not older than their pictures). A file already there as the pack has it is left; files of the assets the
    /// pack does not have are removed, so that what is there is the pack, byte for byte. Throws
    /// <see cref="InvalidDataException"/> where the key does not open it or a file is not what the manifest says.
    /// </summary>
    public static void Install(string pack, string target, byte[] key, Action<int, int> progress)
    {
        var manifest = Read(pack) ?? throw new InvalidDataException("The pack has no manifest.");
        if (!Opens(manifest, key))
            throw new InvalidDataException("This game copy does not open the pack.");
        var done = 0;
        Each(manifest.Files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, entry =>
        {
            var file = System.IO.Path.Combine(target, entry.Path);
            if (!(File.Exists(file) && new FileInfo(file).Length == entry.Size && Time(file) == entry.Time))
            {
                var bytes = Open(File.ReadAllBytes(System.IO.Path.Combine(pack, entry.Path)), entry.Path, key);
                if (bytes.Length != entry.Size || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{entry.Path} is not as the pack's list has it.");
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                Write(file, bytes);
                File.SetLastWriteTimeUtc(file, DateTime.UnixEpoch.AddMilliseconds(entry.Time));
            }
            var count = Interlocked.Increment(ref done);
            if (count % 100 == 0 || count == manifest.Files.Count)
                progress(count, manifest.Files.Count);
        });
        var wanted = manifest.Files.Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(target, "*", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(target, path).Replace('\\', '/');
            if (Belongs(relative) && !wanted.Contains(relative))
                File.Delete(path);
        }
        File.WriteAllText(System.IO.Path.Combine(target, OpenedName), manifest.Id);
    }

    /// <summary>The pack <paramref name="target"/> was opened from, by its <see cref="Manifest.Id"/>.</summary>
    public static string? Opened(string target)
    {
        var file = System.IO.Path.Combine(target, OpenedName);
        return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
    }

    /// <summary>Seals <paramref name="plain"/>: the mark, a fresh nonce, the tag, then the sealed bytes; the path is bound in.</summary>
    public static byte[] Seal(byte[] plain, string path, byte[] key)
    {
        var result = new byte[Magic.Length + NonceSize + TagSize + plain.Length];
        Magic.CopyTo(result, 0);
        var nonce = result.AsSpan(Magic.Length, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, result.AsSpan(Magic.Length + NonceSize + TagSize), result.AsSpan(Magic.Length + NonceSize, TagSize), Encoding.UTF8.GetBytes(path));
        return result;
    }

    /// <summary>Opens what <see cref="Seal"/> made; throws <see cref="InvalidDataException"/> where key, path or bytes are not its.</summary>
    public static byte[] Open(byte[] sealedBytes, string path, byte[] key)
    {
        var head = Magic.Length + NonceSize + TagSize;
        if (sealedBytes.Length < head || !sealedBytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException($"{path} is not sealed by the pack.");
        var plain = new byte[sealedBytes.Length - head];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(sealedBytes.AsSpan(Magic.Length, NonceSize), sealedBytes.AsSpan(head), sealedBytes.AsSpan(Magic.Length + NonceSize, TagSize), plain, Encoding.UTF8.GetBytes(path));
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException($"{path} does not open with this key.");
        }
        return plain;
    }

    /// <summary>
    /// Runs <paramref name="body"/> for every item on four threads, and throws the first failure as itself (not wrapped),
    /// so the import's handling of a broken pack or a full disk catches it.
    /// </summary>
    private static void Each<T>(IEnumerable<T> items, ParallelOptions options, Action<T> body)
    {
        try
        {
            Parallel.ForEach(items, options, body);
        }
        catch (AggregateException failure) when (failure.InnerExceptions.Count > 0)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.InnerExceptions[0]).Throw();
        }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private static string Check(byte[] key) => Convert.ToHexString(HMACSHA256.HashData(key, "Machines Remake asset pack check"u8));

    private static string Id(IEnumerable<Entry> entries) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", entries.Select(entry => $"{entry.Path}\t{entry.Sha256}")))))[..16];

    /// <summary>A file's time in milliseconds since 1970, UTC, as kept in the manifest.</summary>
    private static long Time(string file) => (long)(File.GetLastWriteTimeUtc(file) - DateTime.UnixEpoch).TotalMilliseconds;

    /// <summary>Written whole or not at all: an import stopped half way must not leave a broken file behind.</summary>
    private static void Write(string file, byte[] bytes)
    {
        var writing = $"{file}.{Environment.CurrentManagedThreadId}.tmp";
        File.WriteAllBytes(writing, bytes);
        File.Move(writing, file, overwrite: true);
    }
}
