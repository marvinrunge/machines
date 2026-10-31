using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Machines.Import.Video;

/// <summary>
/// Plays RAD Game Tools' Smacker video, which the original's flics are in (the briefing faces and the system,
/// planet and mission pictures in <c>flics/gui</c>, the cut-scenes in <c>flics</c>). Frames are read and decoded
/// one at a time from a seekable stream, so a long film never sits in memory whole. Version 2 (<c>SMK2</c>, all
/// of the original's) and version 4 (<c>SMK4</c>) video are read, with Smacker's own compressed sound or plain PCM;
/// version 4's Bink-coded sound tracks are left out.
/// </summary>
/// <remarks>
/// The file: a 104-byte header, a table of frame sizes and one of frame types, the four Huffman trees the picture
/// is coded with, then the frames. A frame holds, in this order, a palette change (if its type's bit 0 is set), a
/// sound chunk for each track its type's bits 1 to 7 name, and the picture: 4×4 blocks coded with those trees.
/// Unchanged blocks are skipped, so each picture builds on the one before.
/// </remarks>
public sealed class SmackerDecoder : IDisposable
{
    private const int HeaderSize = 104;
    private const uint RingFrameFlag = 1, YInterlacedFlag = 2, YDoubledFlag = 4;

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly bool _version4;
    private readonly int[] _frameSizes;
    private readonly byte[] _frameTypes;
    private readonly long[] _frameOffsets;
    private readonly byte[] _frame;
    private readonly byte[] _pixels;
    private readonly byte[] _palette = new byte[768];
    private readonly byte[] _previousPalette = new byte[768];
    private readonly uint[] _rgba = new uint[256];
    private readonly SmackerAudioTrack?[] _tracksByIndex = new SmackerAudioTrack?[7];
    private readonly SmackerBits _bits = new();
    private readonly SmackerBigTree _maps, _colours, _full, _types;
    private bool _rgbaStale = true;

    public SmackerDecoder(Stream stream, bool leaveOpen = false)
    {
        if (!stream.CanSeek)
            throw new ArgumentException("Smacker needs a seekable stream", nameof(stream));
        (_stream, _leaveOpen) = (stream, leaveOpen);

        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);
        var signature = header[..4];
        _version4 = signature.SequenceEqual("SMK4"u8);
        if (!_version4 && !signature.SequenceEqual("SMK2"u8))
            throw new InvalidDataException("not a Smacker file");
        Width = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        Height = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        FrameCount = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        var rate = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        var treesSize = BinaryPrimitives.ReadInt32LittleEndian(header[52..]);
        if (Width is <= 0 or > 4096 || Height is <= 0 or > 4096 || FrameCount is <= 0 or > 1_000_000 || treesSize is < 0 or > 1 << 24)
            throw new InvalidDataException("Smacker header out of range");

        // A positive rate is milliseconds per frame, a negative one tens of microseconds; none means 10 a second.
        FrameDuration = rate > 0 ? TimeSpan.FromMilliseconds(rate)
            : rate < 0 ? TimeSpan.FromTicks(-(long)rate * 100)
            : TimeSpan.FromMilliseconds(100);
        HasRingFrame = (flags & RingFrameFlag) != 0;
        YInterlaced = (flags & YInterlacedFlag) != 0;
        YDoubled = (flags & YDoubledFlag) != 0;

        var tracks = new List<SmackerAudioTrack>();
        for (var i = 0; i < 7; i++)
        {
            var audioFlags = BinaryPrimitives.ReadUInt32LittleEndian(header[(72 + 4 * i)..]);
            var largest = BinaryPrimitives.ReadInt32LittleEndian(header[(24 + 4 * i)..]);
            if (SmackerAudioTrack.FromHeader(i, audioFlags, largest) is { } track)
            {
                _tracksByIndex[i] = track;
                tracks.Add(track);
            }
        }
        AudioTracks = tracks;

        // A ring frame, after the last, turns it back into the first for looping; it is read past here.
        var entries = FrameCount + (HasRingFrame ? 1 : 0);
        var table = new byte[entries * 5];
        stream.ReadExactly(table);
        _frameSizes = new int[entries];
        _frameTypes = new byte[entries];
        _frameOffsets = new long[entries];
        var offset = HeaderSize + table.Length + (long)treesSize;
        var largestFrame = 0;
        for (var i = 0; i < entries; i++)
        {
            // The two low bits mark key frames; sizes are whole words.
            var size = (int)(BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(4 * i)) & ~3u);
            _frameSizes[i] = size;
            _frameTypes[i] = table[4 * entries + i];
            _frameOffsets[i] = offset;
            offset += size;
            largestFrame = Math.Max(largestFrame, size);
        }
        if (offset > stream.Length)
            throw new InvalidDataException("Smacker file is cut short");

        var trees = new byte[treesSize];
        stream.ReadExactly(trees);
        _bits.Start(trees, 0, trees.Length);
        _maps = SmackerBigTree.Read(_bits);
        _colours = SmackerBigTree.Read(_bits);
        _full = SmackerBigTree.Read(_bits);
        _types = SmackerBigTree.Read(_bits);
        if (_bits.Overrun)
            throw new InvalidDataException("Smacker trees are cut short");

        _frame = new byte[largestFrame];
        _pixels = new byte[Width * Height];
        Rewind();
    }

    public static SmackerDecoder Open(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16));

    public int Width { get; }

    /// <summary>Lines in a decoded picture; shown at twice as many when <see cref="YInterlaced"/> or <see cref="YDoubled"/>.</summary>
    public int Height { get; }

    /// <summary>Lines the picture is shown at: <see cref="Height"/>, doubled for interlaced or doubled files.</summary>
    public int DisplayHeight => YInterlaced || YDoubled ? Height * 2 : Height;

    public int FrameCount { get; }

    /// <summary>How long each frame is shown.</summary>
    public TimeSpan FrameDuration { get; }

    public double FramesPerSecond => 1 / FrameDuration.TotalSeconds;

    /// <summary>Each line is shown with a black one under it.</summary>
    public bool YInterlaced { get; }

    /// <summary>Each line is shown twice.</summary>
    public bool YDoubled { get; }

    /// <summary>The file ends with a frame that leads from the last back to the first (not counted in <see cref="FrameCount"/>).</summary>
    public bool HasRingFrame { get; }

    public bool IsVersion4 => _version4;

    /// <summary>The sound tracks the header names, by track number.</summary>
    public IReadOnlyList<SmackerAudioTrack> AudioTracks { get; }

    /// <summary>The frame last decoded, from 0; -1 before the first.</summary>
    public int FrameIndex { get; private set; }

    /// <summary>The picture: <see cref="Width"/> × <see cref="Height"/> palette indices, line by line.</summary>
    public ReadOnlySpan<byte> Pixels => _pixels;

    /// <summary>The 256 colours as red, green and blue bytes (the file's 6-bit values widened to 8).</summary>
    public ReadOnlySpan<byte> Palette => _palette;

    /// <summary>Whether the frame last decoded changed the palette.</summary>
    public bool PaletteChanged { get; private set; }

    /// <summary>Frames whose data ran out before their picture or sound was complete (none in a sound file).</summary>
    public int DamagedFrames { get; private set; }

    /// <summary>Back to before the first frame, as when the file was opened.</summary>
    public void Rewind()
    {
        FrameIndex = -1;
        Array.Clear(_pixels);
        Array.Clear(_palette);
        _rgbaStale = true;
        PaletteChanged = false;
        foreach (var track in AudioTracks)
            track.Length = 0;
    }

    /// <summary>
    /// Decodes the next frame: its picture into <see cref="Pixels"/> and <see cref="Palette"/>, its sound into each
    /// track's <see cref="SmackerAudioTrack.Pcm"/>. False after the last frame.
    /// </summary>
    public bool NextFrame()
    {
        if (FrameIndex + 1 >= FrameCount)
            return false;
        FrameIndex++;
        var size = _frameSizes[FrameIndex];
        var type = _frameTypes[FrameIndex];
        _stream.Position = _frameOffsets[FrameIndex];
        _stream.ReadExactly(_frame, 0, size);

        var at = 0;
        PaletteChanged = (type & 1) != 0;
        if (PaletteChanged)
        {
            // The chunk's first byte is its length in words, that byte included.
            var length = _frame[0] * 4;
            if (length == 0 || length > size)
                throw new InvalidDataException($"frame {FrameIndex}: palette chunk out of range");
            ReadPalette(_frame.AsSpan(1, length - 1));
            at = length;
        }
        var damaged = false;
        for (var i = 0; i < 7; i++)
        {
            if (_tracksByIndex[i] is { } empty)
                empty.Length = 0;
            if ((type & (2 << i)) == 0)
                continue;
            var length = ChunkLength(at, size);
            if (_tracksByIndex[i] is { } track)
                damaged |= track.Decode(_frame, at + 4, length - 4, _bits);
            at += length;
        }
        damaged |= DecodePicture(at, size);
        if (damaged)
            DamagedFrames++;
        return true;
    }

    /// <summary>
    /// The whole of one track's sound, read straight through the file with no picture decoded (for a player that
    /// wants it ahead of time); the decoder is left rewound. Empty when the file has no such track.
    /// </summary>
    public byte[] ReadAllAudio(int trackIndex)
    {
        if (trackIndex is < 0 or > 6 || _tracksByIndex[trackIndex] is not { } track)
            return [];
        var output = new MemoryStream();
        for (var frame = 0; frame < FrameCount; frame++)
        {
            var type = _frameTypes[frame];
            if ((type & (2 << trackIndex)) == 0)
                continue;
            var size = _frameSizes[frame];
            _stream.Position = _frameOffsets[frame];
            // Read the frame only as far as this track's chunk.
            var at = 0;
            if ((type & 1) != 0)
            {
                _stream.ReadExactly(_frame, 0, 1);
                at = _frame[0] * 4;
            }
            for (var i = 0; i <= trackIndex; i++)
            {
                if ((type & (2 << i)) == 0)
                    continue;
                if (at + 4 > size)
                    throw new InvalidDataException($"frame {frame}: sound chunk out of range");
                _stream.Position = _frameOffsets[frame] + at;
                _stream.ReadExactly(_frame, at, 4);
                var length = ChunkLength(at, size);
                if (i == trackIndex)
                {
                    _stream.ReadExactly(_frame, at + 4, length - 4);
                    if (track.Decode(_frame, at + 4, length - 4, _bits))
                        DamagedFrames++;
                    output.Write(track.Pcm);
                }
                at += length;
            }
        }
        Rewind();
        return output.ToArray();
    }

    /// <summary>
    /// The picture as 8-bit red, green, blue and alpha, <see cref="Width"/> × <see cref="DisplayHeight"/>: interlaced
    /// files get a black line under each, doubled ones each line twice.
    /// </summary>
    public void CopyRgba(Span<byte> rgba)
    {
        if (rgba.Length < Width * DisplayHeight * 4)
            throw new ArgumentException("buffer too small", nameof(rgba));
        if (_rgbaStale)
        {
            for (var i = 0; i < 256; i++)
                _rgba[i] = _palette[3 * i] | (uint)_palette[3 * i + 1] << 8 | (uint)_palette[3 * i + 2] << 16 | 0xFF000000;
            _rgbaStale = false;
        }
        var target = MemoryMarshal.Cast<byte, uint>(rgba);
        var rowStep = DisplayHeight / Height;
        for (var y = 0; y < Height; y++)
        {
            var source = _pixels.AsSpan(y * Width, Width);
            var line = target.Slice(y * rowStep * Width, Width);
            for (var x = 0; x < source.Length; x++)
                line[x] = _rgba[source[x]];
            if (YDoubled)
                line.CopyTo(target.Slice((y * 2 + 1) * Width, Width));
            else if (YInterlaced)
                target.Slice((y * 2 + 1) * Width, Width).Fill(0xFF000000);
        }
    }

    public void Dispose()
    {
        if (!_leaveOpen)
            _stream.Dispose();
    }

    private int ChunkLength(int at, int size)
    {
        if (at + 4 > size)
            throw new InvalidDataException($"frame {FrameIndex}: sound chunk out of range");
        var length = BinaryPrimitives.ReadInt32LittleEndian(_frame.AsSpan(at));
        if (length < 4 || at + length > size)
            throw new InvalidDataException($"frame {FrameIndex}: sound chunk out of range");
        return length;
    }

    /// <summary>
    /// A palette change, against the palette before it: a byte with the top bit set keeps that many colours (its
    /// low 7 bits plus one), one with the next bit set copies that many (low 6 bits plus one) from the old palette
    /// starting at the colour the following byte names, and any other is a new colour's red, green and blue.
    /// </summary>
    private void ReadPalette(ReadOnlySpan<byte> chunk)
    {
        _palette.CopyTo(_previousPalette, 0);
        var at = 0;
        for (var colour = 0; colour < 256;)
        {
            if (at >= chunk.Length)
                throw new InvalidDataException($"frame {FrameIndex}: palette chunk ends early");
            var code = chunk[at++];
            if ((code & 0x80) != 0)
            {
                colour += (code & 0x7F) + 1;
            }
            else if ((code & 0x40) != 0)
            {
                var count = (code & 0x3F) + 1;
                if (at >= chunk.Length)
                    throw new InvalidDataException($"frame {FrameIndex}: palette chunk ends early");
                var from = chunk[at++];
                if (from + count > 256)
                    throw new InvalidDataException($"frame {FrameIndex}: palette copy out of range");
                count = Math.Min(count, 256 - colour);
                _previousPalette.AsSpan(3 * from, 3 * count).CopyTo(_palette.AsSpan(3 * colour));
                colour += count;
            }
            else
            {
                if (at + 2 > chunk.Length)
                    throw new InvalidDataException($"frame {FrameIndex}: palette chunk ends early");
                _palette[3 * colour] = Widen(code);
                _palette[3 * colour + 1] = Widen(chunk[at++]);
                _palette[3 * colour + 2] = Widen(chunk[at++]);
                colour++;
            }
        }
        _rgbaStale = true;
    }

    /// <summary>A 6-bit colour value to 8 bits, its top bits repeated below: 63 is 255.</summary>
    private static byte Widen(byte value)
    {
        value &= 0x3F;
        return (byte)(value << 2 | value >> 4);
    }

    private const int MonoBlock = 0, FullBlock = 1, VoidBlock = 2, SolidBlock = 3;

    /// <summary>
    /// The picture, block by block from the top left, each a run of one type: two colours drawn by a 16-bit map
    /// (mono), every pixel's colour (full), the previous frame's block (void), or one colour (solid). The type's
    /// bits 2 to 7 index the run length, its high byte is a solid block's colour.
    /// </summary>
    private bool DecodePicture(int start, int end)
    {
        var bits = _bits;
        bits.Start(_frame, start, end);
        _maps.ResetCache();
        _colours.ResetCache();
        _full.ResetCache();
        _types.ResetCache();

        var width = Width;
        var columns = width / 4;
        var blocks = columns * (Height / 4);
        var pixels = _pixels;
        for (var block = 0; block < blocks;)
        {
            var type = _types.Decode(bits);
            var last = Math.Min(blocks, block + RunLength((type >> 2) & 0x3F));
            switch (type & 3)
            {
                case MonoBlock:
                    for (; block < last; block++)
                    {
                        var colours = _colours.Decode(bits);
                        var map = _maps.Decode(bits);
                        byte low = (byte)colours, high = (byte)(colours >> 8);
                        var at = BlockStart(block, columns, width);
                        for (var row = 0; row < 4; row++, at += width, map >>= 4)
                        {
                            pixels[at] = (map & 1) != 0 ? high : low;
                            pixels[at + 1] = (map & 2) != 0 ? high : low;
                            pixels[at + 2] = (map & 4) != 0 ? high : low;
                            pixels[at + 3] = (map & 8) != 0 ? high : low;
                        }
                    }
                    break;
                case FullBlock:
                    // Version 4 also has full blocks of 2×2 pixels (a 1 bit) and of 2×1 pixels (01).
                    var mode = !_version4 ? 0 : bits.Bit() != 0 ? 1 : bits.Bit() != 0 ? 2 : 0;
                    for (; block < last; block++)
                        FillFull(BlockStart(block, columns, width), width, mode);
                    break;
                case VoidBlock:
                    block = last;
                    break;
                default:
                    var colour = (byte)(type >> 8);
                    for (; block < last; block++)
                    {
                        var at = BlockStart(block, columns, width);
                        for (var row = 0; row < 4; row++, at += width)
                            pixels.AsSpan(at, 4).Fill(colour);
                    }
                    break;
            }
        }
        return bits.Overrun;
    }

    /// <summary>A full block: each code is two pixels, the low byte on the left; a line's right pair comes first.</summary>
    private void FillFull(int at, int width, int mode)
    {
        var pixels = _pixels;
        switch (mode)
        {
            case 0:
                for (var row = 0; row < 4; row++, at += width)
                {
                    var right = _full.Decode(_bits);
                    var left = _full.Decode(_bits);
                    pixels[at] = (byte)left;
                    pixels[at + 1] = (byte)(left >> 8);
                    pixels[at + 2] = (byte)right;
                    pixels[at + 3] = (byte)(right >> 8);
                }
                break;
            case 1:
                for (var half = 0; half < 2; half++)
                {
                    var pair = _full.Decode(_bits);
                    for (var row = 0; row < 2; row++, at += width)
                    {
                        pixels[at] = pixels[at + 1] = (byte)pair;
                        pixels[at + 2] = pixels[at + 3] = (byte)(pair >> 8);
                    }
                }
                break;
            default:
                for (var half = 0; half < 2; half++)
                {
                    var right = _full.Decode(_bits);
                    var left = _full.Decode(_bits);
                    for (var row = 0; row < 2; row++, at += width)
                    {
                        pixels[at] = (byte)left;
                        pixels[at + 1] = (byte)(left >> 8);
                        pixels[at + 2] = (byte)right;
                        pixels[at + 3] = (byte)(right >> 8);
                    }
                }
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BlockStart(int block, int columns, int width) => block / columns * 4 * width + block % columns * 4;

    /// <summary>Runs of 1 to 59 blocks, then 128, 256, 512, 1024 and 2048.</summary>
    private static int RunLength(int index) => index < 59 ? index + 1 : 128 << (index - 59);
}

/// <summary>One of a Smacker file's up to seven sound tracks.</summary>
public sealed class SmackerAudioTrack
{
    private const uint CompressedFlag = 0x8000_0000, SixteenBitFlag = 0x2000_0000, StereoFlag = 0x1000_0000,
        BinkFlags = 0x0C00_0000;

    private byte[] _pcm;

    private SmackerAudioTrack(int index, uint flags, int largestChunk)
    {
        Index = index;
        Rate = (int)(flags & 0xFFFFFF);
        Bits = (flags & SixteenBitFlag) != 0 ? 16 : 8;
        Channels = (flags & StereoFlag) != 0 ? 2 : 1;
        Compressed = (flags & CompressedFlag) != 0;
        _pcm = new byte[Math.Max(largestChunk, 0)];
    }

    /// <summary>The track a header's sound flags describe (rate in the low 24 bits); null for none, or a Bink-coded one.</summary>
    internal static SmackerAudioTrack? FromHeader(int index, uint flags, int largestChunk) =>
        (flags & 0xFFFFFF) == 0 || (flags & BinkFlags) != 0 ? null : new SmackerAudioTrack(index, flags, largestChunk);

    /// <summary>The track's number in the file, 0 to 6.</summary>
    public int Index { get; }

    /// <summary>Samples a second.</summary>
    public int Rate { get; }

    /// <summary>8 (unsigned samples) or 16 (signed, little-endian).</summary>
    public int Bits { get; }

    public int Channels { get; }

    /// <summary>Whether the file codes the sound with Smacker's Huffman-coded differences, rather than plain PCM.</summary>
    public bool Compressed { get; }

    public int BytesPerSecond => Rate * Channels * Bits / 8;

    /// <summary>
    /// The sound the frame last decoded carries, channels interleaved. Often empty: the first frames carry the sound
    /// ahead of the picture, so later ones may have none.
    /// </summary>
    public ReadOnlySpan<byte> Pcm => _pcm.AsSpan(0, Length);

    internal int Length { get; set; }

    /// <summary>Decodes one frame's chunk, <paramref name="length"/> bytes at <paramref name="start"/>; true when its data ran out early.</summary>
    internal bool Decode(byte[] frame, int start, int length, SmackerBits bits)
    {
        Length = 0;
        if (!Compressed)
        {
            frame.AsSpan(start, length).CopyTo(Store(length));
            return false;
        }
        // The chunk starts with the sound's unpacked size.
        if (length < 4)
            return true;
        var size = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(start));
        if (size <= 0)
            return false;
        bits.Start(frame, start + 4, start + length);
        if (bits.Bit() == 0)
            return false; // a chunk with no sound
        var stereo = bits.Bit() != 0;
        var sixteen = bits.Bit() != 0;
        if (stereo != (Channels == 2) || sixteen != (Bits == 16))
            throw new InvalidDataException($"sound track {Index}: chunk format differs from the header's");

        var trees = new SmackerTree[sixteen ? 2 * Channels : Channels];
        for (var i = 0; i < trees.Length; i++)
            trees[i] = SmackerTree.ReadByteTree(bits);

        var samples = sixteen ? size / 2 : size;
        if (samples < Channels)
            return bits.Overrun;
        var output = Store(sixteen ? samples * 2 : samples);
        // The first sample of each channel is given whole, the last channel's first; the rest are differences from
        // the channel's sample before, which wrap rather than clip. A 16-bit first sample comes high byte first.
        Span<int> previous = stackalloc int[2];
        for (var channel = Channels - 1; channel >= 0; channel--)
            previous[channel] = sixteen ? bits.Bits(8) << 8 | bits.Bits(8) : bits.Bits(8);
        if (sixteen)
        {
            var words = MemoryMarshal.Cast<byte, short>(output);
            for (var i = 0; i < Channels; i++)
                words[i] = (short)previous[i];
            for (var i = Channels; i < samples; i++)
            {
                var channel = i & (Channels - 1);
                var low = trees[2 * channel].Decode(bits);
                var high = trees[2 * channel + 1].Decode(bits);
                previous[channel] = (short)(previous[channel] + (short)(low | high << 8));
                words[i] = (short)previous[channel];
            }
        }
        else
        {
            for (var i = 0; i < Channels; i++)
                output[i] = (byte)previous[i];
            for (var i = Channels; i < samples; i++)
            {
                var channel = i & (Channels - 1);
                previous[channel] = (byte)(previous[channel] + trees[channel].Decode(bits));
                output[i] = (byte)previous[channel];
            }
        }
        return bits.Overrun;
    }

    private Span<byte> Store(int length)
    {
        if (_pcm.Length < length)
            _pcm = new byte[length];
        Length = length;
        return _pcm.AsSpan(0, length);
    }
}

/// <summary>Reads a Smacker bitstream: each byte's bits lowest first, values with their first bit lowest.</summary>
internal sealed class SmackerBits
{
    private byte[] _data = [];
    private int _position, _end;
    private ulong _cache;
    private int _count;

    /// <summary>Whether more bits were taken than the data holds; the missing ones read as zeros.</summary>
    public bool Overrun { get; private set; }

    public void Start(byte[] data, int start, int end)
    {
        (_data, _position, _end) = (data, start, end);
        (_cache, _count, Overrun) = (0, 0, false);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Fill()
    {
        while (_count <= 56 && _position < _end)
        {
            _cache |= (ulong)_data[_position++] << _count;
            _count += 8;
        }
    }

    /// <summary>The next <paramref name="count"/> bits (up to 32), not taken.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Peek(int count)
    {
        if (_count < count)
            Fill();
        return (uint)(_cache & ((1UL << count) - 1));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Skip(int count)
    {
        if (_count < count)
        {
            Fill();
            if (_count < count)
            {
                (_cache, _count, Overrun) = (0, 0, true);
                return;
            }
        }
        _cache >>= count;
        _count -= count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Bit()
    {
        if (_count == 0)
        {
            Fill();
            if (_count == 0)
            {
                Overrun = true;
                return 0;
            }
        }
        var bit = (int)(_cache & 1);
        _cache >>= 1;
        _count--;
        return bit;
    }

    public int Bits(int count)
    {
        var value = (int)Peek(count);
        Skip(count);
        return value;
    }
}

/// <summary>
/// A Huffman tree as Smacker stores it: depth first, a 1 bit for a branch (its 0 side follows first), a 0 bit for
/// a leaf and then its value. Decoding looks the next bits up in a table and walks on from there for longer codes.
/// </summary>
internal sealed class SmackerTree
{
    private const int TableBits = 12;

    // A branch holds the index of its 1 side, its 0 side being the next entry; a leaf holds its value's complement.
    private readonly int[] _nodes;
    private readonly int[] _table;
    private readonly int _tableBits;

    private SmackerTree(int[] nodes, int depth)
    {
        _nodes = nodes;
        _tableBits = Math.Min(depth, TableBits);
        // Each entry: the node reached by the table's bits, and how many of them it took to get there.
        _table = new int[1 << _tableBits];
        for (var code = 0; code < _table.Length; code++)
        {
            int node = 0, used = 0;
            while (used < _tableBits && _nodes[node] >= 0)
            {
                node = (code >> used & 1) != 0 ? _nodes[node] : node + 1;
                used++;
            }
            _table[code] = node << 4 | used;
        }
    }

    /// <summary>A tree the file leaves out: every value is 0 and takes no bits.</summary>
    public static SmackerTree Zero { get; } = new([~0], 0);

    /// <summary>Reads a tree of bytes: a 1 bit, the tree and a closing 0 bit; or a 0 bit for none.</summary>
    public static SmackerTree ReadByteTree(SmackerBits bits)
    {
        if (bits.Bit() == 0)
            return Zero;
        var tree = Read(bits, () => bits.Bits(8), 256);
        bits.Skip(1);
        return tree;
    }

    /// <summary>Reads the tree's branches and leaves, with <paramref name="leaf"/> reading each leaf's value.</summary>
    public static SmackerTree Read(SmackerBits bits, Func<int> leaf, int maxDepth)
    {
        var nodes = new List<int>();
        var deepest = 0;
        ReadNode(0);
        if (bits.Overrun)
            throw new InvalidDataException("Smacker tree is cut short");
        return new SmackerTree([.. nodes], deepest);

        void ReadNode(int depth)
        {
            if (depth > maxDepth)
                throw new InvalidDataException("Smacker tree too deep");
            var at = nodes.Count;
            nodes.Add(0);
            if (bits.Bit() == 0)
            {
                nodes[at] = ~leaf();
                deepest = Math.Max(deepest, depth);
                return;
            }
            ReadNode(depth + 1);
            nodes[at] = nodes.Count;
            ReadNode(depth + 1);
            if (bits.Overrun)
                throw new InvalidDataException("Smacker tree is cut short");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Decode(SmackerBits bits)
    {
        var entry = _table[bits.Peek(_tableBits)];
        bits.Skip(entry & 15);
        var node = entry >> 4;
        int value;
        while ((value = _nodes[node]) >= 0)
            node = bits.Bit() != 0 ? value : node + 1;
        return ~value;
    }
}

/// <summary>
/// One of the four 16-bit trees a Smacker picture is coded with (block maps, mono block colours, full block pixels,
/// block types). Each leaf's value is built from two byte trees, low byte then high. Three escape values mark the
/// leaves that stand for the three values last decoded, newest first; that memory starts at zeros each frame.
/// </summary>
internal sealed class SmackerBigTree
{
    private const int Recent = 0x10000;

    private readonly SmackerTree? _tree;
    private readonly int[] _recent = new int[3];

    private SmackerBigTree(SmackerTree? tree) => _tree = tree;

    /// <summary>Reads a tree: a 1 bit, the byte trees, the three escapes, the tree and a closing 0 bit; or a 0 bit for none.</summary>
    public static SmackerBigTree Read(SmackerBits bits)
    {
        if (bits.Bit() == 0)
            return new SmackerBigTree(null);
        var low = SmackerTree.ReadByteTree(bits);
        var high = SmackerTree.ReadByteTree(bits);
        var escapes = new[] { bits.Bits(16), bits.Bits(16), bits.Bits(16) };
        var tree = SmackerTree.Read(bits, () =>
        {
            var value = low.Decode(bits) | high.Decode(bits) << 8;
            var escape = Array.IndexOf(escapes, value);
            return escape >= 0 ? Recent + escape : value;
        }, 1024);
        bits.Skip(1);
        return new SmackerBigTree(tree);
    }

    public void ResetCache() => Array.Clear(_recent);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Decode(SmackerBits bits)
    {
        if (_tree is null)
            return 0;
        var value = _tree.Decode(bits);
        if (value >= Recent)
            value = _recent[value - Recent];
        if (value != _recent[0])
        {
            _recent[2] = _recent[1];
            _recent[1] = _recent[0];
            _recent[0] = value;
        }
        return value;
    }
}
