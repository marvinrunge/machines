using System.IO.Compression;
using System.Text;

namespace Machines.Game;

/// <summary>
/// The original CDs of Machines: Wired for War, in a drive or as a disc image (<c>.iso</c>), so that a player needs no
/// installed copy: CD 1 holds the game packed in an InstallShield 5 cabinet (<c>data1.hdr</c> and <c>data1.cab</c>),
/// with the films and pictures it does not pack lying beside it, and CD 2 holds the campaign's films. <see cref="Install"/>
/// unpacks CD 1 into a folder laid out as the original's setup would have left it, which <see cref="ClassicImport"/>
/// then imports from, and <see cref="AddFilms"/> adds CD 2's films to it. Nothing here needs Godot, so the tests run it.
/// </summary>
public static class GameDisc
{
    public enum Kind
    {
        None,
        One,
        Two,
    }

    /// <summary>What <paramref name="disc"/> is: CD 1 (the cabinet), CD 2 (the films without it) or neither.</summary>
    public static Kind Which(IDiscFiles disc) =>
        disc.Has("data1.hdr") && disc.Has("data1.cab") ? Kind.One
        : disc.Has(@"flics\podland.smk") || disc.Has(@"flics\outro.smk") ? Kind.Two
        : Kind.None;

    /// <summary>
    /// Opens a drive's root, a folder or a disc image as a disc, or null when it is none of the game's CDs (or cannot
    /// be read).
    /// </summary>
    public static IDiscFiles? Open(string path)
    {
        try
        {
            IDiscFiles? disc = File.Exists(path) && path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) ? new IsoImage(path)
                : Directory.Exists(path) ? new FolderDisc(path)
                : null;
            if (disc is not null && Which(disc) != Kind.None)
                return disc;
            disc?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
        }
        return null;
    }

    /// <summary>
    /// The game's CDs on this computer: in the CD and DVD drives (a disc image opened in Windows shows as one too), and
    /// the disc images in the player's Downloads, Desktop and Documents and in the folders there named after the game.
    /// </summary>
    public static IReadOnlyList<string> Find()
    {
        var found = new List<string>(Drives());
        var home = GameLocator.Home;
        foreach (var folder in new[] { "Downloads", "Desktop", "Documents" }.Select(name => Path.Combine(home, name)))
            found.AddRange(Images(folder));
        return found;
    }

    /// <summary>The CD and DVD drives that hold one of the game's CDs.</summary>
    public static IEnumerable<string> Drives()
    {
        if (!OperatingSystem.IsWindows())
            yield break;
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool held;
            try
            {
                held = drive.DriveType == DriveType.CDRom && drive.IsReady;
            }
            catch (IOException)
            {
                continue;
            }
            if (!held)
                continue;
            using var disc = Open(drive.RootDirectory.FullName);
            if (disc is not null)
                yield return drive.RootDirectory.FullName;
        }
    }

    /// <summary>A drive holding the game's CD 1, if one does.</summary>
    public static string? InDrive() => First(Drives(), Kind.One);

    /// <summary>The disc images of the game's CDs in <paramref name="folder"/> and one level down in folders named after it.</summary>
    public static IEnumerable<string> Images(string folder)
    {
        var folders = new List<string> { folder };
        try
        {
            if (Directory.Exists(folder))
                folders.AddRange(Directory.GetDirectories(folder).Where(child => Path.GetFileName(child).Contains("machines", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        foreach (var place in folders)
        {
            string[] images;
            try
            {
                images = Directory.Exists(place) ? Directory.GetFiles(place, "*.iso") : [];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var image in images.Order(StringComparer.OrdinalIgnoreCase))
                if (Open(image) is { } disc)
                {
                    disc.Dispose();
                    yield return image;
                }
        }
    }

    /// <summary>The first of <paramref name="places"/> that is the game's CD of that kind.</summary>
    public static string? First(IEnumerable<string> places, Kind kind) =>
        places.FirstOrDefault(place =>
        {
            using var disc = Open(place);
            return disc is not null && Which(disc) == kind;
        });

    /// <summary>
    /// CD 2 to go with CD 1 at <paramref name="one"/>: a disc image of it in the same folder, else one found on this
    /// computer.
    /// </summary>
    public static string? Second(string one)
    {
        var folder = File.Exists(one) ? Path.GetDirectoryName(one) : null;
        var beside = folder is null ? [] : Images(folder);
        return First(beside.Concat(Find()), Kind.Two);
    }

    /// <summary>
    /// Unpacks CD 1 into <paramref name="target"/>: every file of the cabinet, those it packs and those it only names
    /// from the disc, as the original's setup laid them out, without DirectX's setup and the disc's start program and
    /// adverts. <paramref name="progress"/> hears how far it has come.
    /// </summary>
    public static void Install(IDiscFiles disc, string target, Action<int, int> progress)
    {
        using var header = disc.Open("data1.hdr");
        var cabinet = InstallShieldCabinet.Read(header);
        using var data = disc.Open("data1.cab");
        var files = cabinet.Files.Where(file => Wanted(file.Path)).ToList();
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var destination = Path.Combine(target, file.Path.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var output = File.Create(destination))
            {
                if (file.Packed)
                    cabinet.Extract(file, data, output);
                else
                {
                    using var loose = disc.Open(file.Path);
                    loose.CopyTo(output);
                }
            }
            if (i % 50 == 0 || i == files.Count - 1)
                progress(i + 1, files.Count);
        }
    }

    /// <summary>Copies CD 2's films into the <c>flics</c> folder of the copy in <paramref name="target"/>.</summary>
    public static void AddFilms(IDiscFiles disc, string target, Action<int, int> progress)
    {
        var films = disc.Files(@"flics").ToList();
        for (var i = 0; i < films.Count; i++)
        {
            var destination = Path.Combine(target, films[i].Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var input = disc.Open(films[i]))
            using (var output = File.Create(destination))
                input.CopyTo(output);
            progress(i + 1, films.Count);
        }
    }

    /// <summary>The copy has CD 2's films.</summary>
    public static bool HasFilms(string copy) => File.Exists(Path.Combine(copy, "flics", "podland.smk"));

    private static bool Wanted(string path)
    {
        var top = path.Split('\\')[0];
        if (top.Equals("Directx6", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!path.Contains('\\'))
            return !(path.StartsWith("autorun.", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".avi", StringComparison.OrdinalIgnoreCase));
        return true;
    }
}

/// <summary>The files of a disc, by their path from its root with backslashes, found whatever their case.</summary>
public interface IDiscFiles : IDisposable
{
    bool Has(string path);

    Stream Open(string path);

    /// <summary>Every file in <paramref name="folder"/> and below it.</summary>
    IEnumerable<string> Files(string folder);
}

/// <summary>A disc in a drive, or a folder holding what is on it.</summary>
public sealed class FolderDisc(string root) : IDiscFiles
{
    public bool Has(string path) => Find(path) is not null;

    public Stream Open(string path) => File.OpenRead(Find(path) ?? throw new FileNotFoundException(path));

    public IEnumerable<string> Files(string folder) => Find(folder) is { } found && Directory.Exists(found)
        ? Directory.GetFiles(found, "*", SearchOption.AllDirectories).Select(file => Path.GetRelativePath(root, file).Replace('/', '\\'))
        : [];

    public void Dispose()
    {
    }

    /// <summary>The file or folder at <paramref name="path"/>, its case as the disc has it.</summary>
    private string? Find(string path)
    {
        var here = root;
        foreach (var part in path.Split('\\'))
        {
            if (!Directory.Exists(here))
                return null;
            var match = Directory.EnumerateFileSystemEntries(here).FirstOrDefault(entry => Path.GetFileName(entry).Equals(part, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return null;
            here = match;
        }
        return here;
    }
}

/// <summary>
/// A disc image in the ISO 9660 format, read as it is: its Joliet names where it has them (as the game's CDs do), else
/// the plain ones without their ";1".
/// </summary>
public sealed class IsoImage : IDiscFiles
{
    private const int Sector = 2048;

    private readonly FileStream _file;
    private readonly Dictionary<string, (long Start, long Size, bool Folder)> _entries = new(StringComparer.OrdinalIgnoreCase);

    public IsoImage(string path)
    {
        _file = File.OpenRead(path);
        try
        {
            byte[]? primary = null, joliet = null;
            for (var sector = 16; sector < 64; sector++)
            {
                var descriptor = Read(sector * (long)Sector, Sector);
                if (descriptor[0] == 255 || Encoding.ASCII.GetString(descriptor, 1, 5) != "CD001")
                    break;
                if (descriptor[0] == 1)
                    primary = descriptor;
                else if (descriptor[0] == 2 && descriptor[88] == 0x25 && descriptor[89] == 0x2f && descriptor[90] is 0x40 or 0x43 or 0x45)
                    joliet = descriptor;
            }
            var volume = joliet ?? primary ?? throw new InvalidDataException("Not a disc image.");
            var root = Record(volume, 156);
            Walk(root.Start, root.Size, "", joliet is not null, 0);
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public bool Has(string path) => _entries.TryGetValue(path, out var entry) && !entry.Folder;

    public Stream Open(string path)
    {
        if (!_entries.TryGetValue(path, out var entry) || entry.Folder)
            throw new FileNotFoundException(path);
        return new Slice(_file, entry.Start, entry.Size);
    }

    public IEnumerable<string> Files(string folder) =>
        _entries.Where(entry => !entry.Value.Folder && entry.Key.StartsWith(folder + "\\", StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Key).ToList();

    public void Dispose() => _file.Dispose();

    private void Walk(long start, long size, string path, bool joliet, int depth)
    {
        if (depth > 16)
            return;
        var data = Read(start, (int)size);
        var at = 0;
        while (at < data.Length)
        {
            if (data[at] == 0)
            {
                at = (at / Sector + 1) * Sector;
                continue;
            }
            var length = data[at];
            var record = Record(data, at);
            at += length;
            var nameLength = data[at - length + 32];
            if (nameLength == 1 && data[at - length + 33] is 0 or 1)
                continue;
            var name = joliet
                ? Encoding.BigEndianUnicode.GetString(data, at - length + 33, nameLength & ~1)
                : Encoding.Latin1.GetString(data, at - length + 33, nameLength);
            var semicolon = name.IndexOf(';');
            if (semicolon >= 0)
                name = name[..semicolon];
            if (!joliet && name.EndsWith('.'))
                name = name[..^1];
            var full = path.Length == 0 ? name : path + "\\" + name;
            _entries[full] = (record.Start, record.Size, record.Folder);
            if (record.Folder)
                Walk(record.Start, record.Size, full, joliet, depth + 1);
        }
    }

    private static (long Start, long Size, bool Folder) Record(byte[] data, int at) =>
        (BitConverter.ToUInt32(data, at + 2) * (long)Sector, BitConverter.ToUInt32(data, at + 10), (data[at + 25] & 2) != 0);

    private byte[] Read(long offset, int count)
    {
        var buffer = new byte[count];
        _file.Position = offset;
        _file.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>A file inside the image, read from it as needed.</summary>
    private sealed class Slice(FileStream image, long start, long size) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => size;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, size);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var left = (int)Math.Min(count, size - _position);
            if (left <= 0)
                return 0;
            image.Position = start + _position;
            var read = image.Read(buffer, offset, left);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => size + offset,
        };

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// An InstallShield 5 cabinet, as the game's CD 1 has it: the header file names every file with its folder, size and
/// where its data is, and the cabinet holds the packed ones in pieces of raw deflate, each behind its length. A file
/// marked neither packed nor split lies on the disc itself, at its own path.
/// </summary>
public sealed class InstallShieldCabinet
{
    private const uint Signature = 0x28635349; // "ISc("
    private const int Split = 1, Obfuscated = 2, Compressed = 4, Invalid = 8;

    public sealed record Entry(string Path, long Size, long PackedSize, long Offset, bool Packed);

    public IReadOnlyList<Entry> Files { get; }

    private InstallShieldCabinet(IReadOnlyList<Entry> files) => Files = files;

    public static InstallShieldCabinet Read(Stream header)
    {
        using var memory = new MemoryStream();
        header.CopyTo(memory);
        var h = memory.ToArray();
        if (h.Length < 20 || BitConverter.ToUInt32(h, 0) != Signature)
            throw new InvalidDataException("data1.hdr is not an InstallShield cabinet.");
        var version = BitConverter.ToUInt32(h, 4);
        var major = version >> 24 == 1 ? (int)((version >> 12) & 0xf) : (int)(version & 0xffff);
        if (major > 100)
            major /= 100;
        if (major != 5)
            throw new InvalidDataException($"This CD's cabinet is InstallShield {major}, only 5 is known.");

        var descriptor = (int)BitConverter.ToUInt32(h, 0x0c);
        var tableAt = descriptor + (int)BitConverter.ToUInt32(h, descriptor + 0x0c);
        var folders = (int)BitConverter.ToUInt32(h, descriptor + 0x1c);
        var count = (int)BitConverter.ToUInt32(h, descriptor + 0x28);
        int Table(int i) => (int)BitConverter.ToUInt32(h, tableAt + i * 4);
        string Text(int at)
        {
            var end = at;
            while (h[end] != 0)
                end++;
            return Encoding.Latin1.GetString(h, at, end - at);
        }

        var names = Enumerable.Range(0, folders).Select(i => Text(tableAt + Table(i))).ToArray();
        var files = new List<Entry>();
        for (var i = 0; i < count; i++)
        {
            var at = tableAt + Table(folders + i);
            var name = Text(tableAt + (int)BitConverter.ToUInt32(h, at));
            var folder = (int)BitConverter.ToUInt32(h, at + 4);
            var flags = BitConverter.ToUInt16(h, at + 8);
            if ((flags & Invalid) != 0 || name.Length == 0)
                continue;
            if ((flags & (Split | Obfuscated)) != 0)
                throw new InvalidDataException($"{name} is split or obfuscated in the cabinet, which this import cannot read.");
            var path = folder < names.Length && names[folder].Length > 0 ? names[folder] + "\\" + name : name;
            files.Add(new Entry(path, BitConverter.ToUInt32(h, at + 10), BitConverter.ToUInt32(h, at + 14),
                BitConverter.ToUInt32(h, at + 0x26), (flags & Compressed) != 0));
        }
        return new InstallShieldCabinet(files);
    }

    /// <summary>Unpacks a packed file from the cabinet <paramref name="data"/> into <paramref name="output"/>.</summary>
    public void Extract(Entry file, Stream data, Stream output)
    {
        data.Position = file.Offset;
        var left = file.PackedSize;
        var length = new byte[2];
        var piece = new byte[ushort.MaxValue];
        long written = 0;
        while (left > 0)
        {
            data.ReadExactly(length);
            var size = BitConverter.ToUInt16(length);
            data.ReadExactly(piece, 0, size);
            using (var inflate = new DeflateStream(new MemoryStream(piece, 0, size), CompressionMode.Decompress))
            {
                var before = output.Position;
                inflate.CopyTo(output);
                written += output.Position - before;
            }
            left -= 2 + size;
        }
        if (written != file.Size)
            throw new InvalidDataException($"{file.Path} came out {written} bytes long instead of {file.Size}.");
    }
}
