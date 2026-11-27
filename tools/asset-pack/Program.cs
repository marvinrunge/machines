using System.Text.Json;
using Machines.Game;

// The asset pack (game/scripts/AssetPack.cs) from the command line, and the release check:
//   pack <classic folder> <game copy> <pack folder> [<part>,...]   seals classic/ (or only those top folders) with the copy's key
//   unpack <pack folder> <game copy> <target folder>              opens it, as the game's import does
//   scan <folder> <list.json>                                      lists a folder's asset files with length, SHA-256 and time
//   compare <wanted> <installed> [<part>,...]                     folders, packs or lists: what the installation lacks or has otherwise
// compare exits with 1 where anything is missing or different, so a release build can stop on it.
return args switch
{
    ["pack", var classic, var copy, var pack, .. var rest] => Pack(classic, copy, pack, rest.FirstOrDefault()),
    ["unpack", var pack, var copy, var target] => Unpack(pack, copy, target),
    ["scan", var folder, var list] => Scan(folder, list),
    ["compare", var wanted, var installed, .. var only] => Compare(wanted, installed, only.FirstOrDefault()),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("asset-pack pack <classic> <game copy> <pack> [parts] | unpack <pack> <game copy> <target> | scan <folder> <list.json> | compare <wanted> <installed> [parts]");
    return 2;
}

static byte[]? KeyOf(string copy)
{
    if (GameDisc.Check(copy) != GameDisc.Proof.Owned)
    {
        Console.Error.WriteLine($"{copy} is no copy of the game (models.bin and the planets checked by GameDisc.Known)");
        return null;
    }
    return AssetPack.Key(copy);
}

static int Pack(string classic, string copy, string pack, string? parts)
{
    if (KeyOf(copy) is not { } key)
        return 1;
    var only = parts?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var manifest = AssetPack.Build(classic, pack, key, (done, total) => Console.Write($"\rsealing {done}/{total}   "),
        only is null ? null : path => only.Contains(path.Split('/')[0]));
    Console.WriteLine($"\rpack {manifest.Id}: {manifest.Files.Count} files, {manifest.Files.Sum(entry => entry.Size) / 1048576} MiB");
    return 0;
}

static int Unpack(string pack, string copy, string target)
{
    if (KeyOf(copy) is not { } key)
        return 1;
    Directory.CreateDirectory(target);
    AssetPack.Install(pack, target, key, (done, total) => Console.Write($"\ropening {done}/{total}   "));
    Console.WriteLine($"\ropened into {target}");
    return 0;
}

static IReadOnlyList<AssetPack.Entry> Load(string place) =>
    File.Exists(place)
        ? JsonSerializer.Deserialize<List<AssetPack.Entry>>(File.ReadAllText(place)) ?? []
        : AssetPack.Read(place)?.Files is { } packed ? packed
        : AssetPack.Scan(place);

static int Scan(string folder, string list)
{
    var entries = AssetPack.Scan(folder);
    File.WriteAllText(list, JsonSerializer.Serialize(entries));
    Console.WriteLine($"{entries.Count} files, {entries.Sum(entry => entry.Size) / 1048576} MiB");
    return 0;
}

static int Compare(string wanted, string installed, string? parts)
{
    var only = parts?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    bool In(AssetPack.Entry entry) => only is null || only.Contains(entry.Path.Split('/')[0]);
    var want = Load(wanted).Where(In).ToList();
    var have = Load(installed).Where(In).ToList();
    var differences = AssetPack.Compare(want, have);
    void Show(string what, IReadOnlyList<string> paths, int most)
    {
        if (paths.Count == 0)
            return;
        Console.WriteLine($"{what}: {paths.Count}");
        foreach (var path in paths.Take(most))
            Console.WriteLine($"  {path}");
        if (paths.Count > most)
            Console.WriteLine($"  ... and {paths.Count - most} more");
    }
    // Per top folder first: where the difference is.
    foreach (var group in differences.Missing.Concat(differences.Differing).GroupBy(path => path.Split('/')[0]).OrderBy(group => group.Key))
        Console.WriteLine($"{group.Key}: {group.Count()} missing or different");
    Show("missing", differences.Missing, 40);
    Show("different", differences.Differing, 40);
    Show("more (caches the game made, or left over)", differences.Extra, 10);
    Console.WriteLine(differences.Same ? $"same: all {want.Count} files are there, byte for byte" : "NOT the same");
    return differences.Same ? 0 : 1;
}
