using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace AdvancedPenumbraModConverter.Core;

/// <summary>
/// An FFXIV animation pack (.pap): a packed header, one info entry per animation, the
/// Havok animation container and one embedded timeline (TMLB) per animation. Layout
/// follows VFXEditor's PapFile/PapAnimation. Everything not explicitly edited, including
/// the Havok and timeline bytes, is preserved byte for byte.
/// </summary>
public sealed class PapFile
{
    /// <summary>One animation of the pack. <see cref="Face"/> is non-zero for facial animations.</summary>
    public sealed record Entry(string Name, short Type, short Binding, int Face)
    {
        public bool IsBody => Face == 0;
    }

    public const int MaxFileSize = 256 * 1024 * 1024;

    // Packed header: magic (4), version (4), count (2), model ID (2), model type and
    // variant (1 each), then the info, Havok and timeline offsets. There is no padding.
    private const int HeaderSize = 26;
    private const int InfoOffsetField = 14;
    private const int HavokOffsetField = 18;
    private const int TimelineOffsetField = 22;
    private const int EntrySize = 40;
    private const int NameSize = 32;

    private readonly byte[] _bytes;

    public PapFile(byte[] data)
    {
        if (data.Length < HeaderSize || data.Length > MaxFileSize || !data.AsSpan(0, 4).SequenceEqual("pap "u8))
            throw new InvalidDataException("Not a PAP file, or its size is invalid.");
        if (ReadInt(data, 4) != 0x00020001) throw new InvalidDataException("Unsupported PAP version.");
        var count = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(8));
        var info = ReadInt(data, InfoOffsetField);
        HavokOffset = ReadInt(data, HavokOffsetField);
        TimelineOffset = ReadInt(data, TimelineOffsetField);
        if (count is < 1 or > 4096 || info < HeaderSize || (long)info + count * EntrySize > HavokOffset ||
            HavokOffset > TimelineOffset || TimelineOffset > data.Length)
            throw new InvalidDataException(
                $"PAP offsets or animation count are invalid (count={count}, info={info}, " +
                $"Havok={HavokOffset}, timeline={TimelineOffset}, size={data.Length}).");

        var entries = ImmutableArray.CreateBuilder<Entry>(count);
        for (var i = 0; i < count; i++)
        {
            var start = info + i * EntrySize;
            var name = data.AsSpan(start, NameSize);
            var end = name.IndexOf((byte)0);
            if (end < 1) throw new InvalidDataException("A PAP animation name is empty or unterminated.");
            var binding = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(start + 34));
            if (binding < 0) throw new InvalidDataException("A PAP binding index is negative.");
            entries.Add(new Entry(Encoding.UTF8.GetString(name[..end]),
                BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(start + 32)), binding, ReadInt(data, start + 36)));
        }

        Entries = entries.MoveToImmutable();
        _bytes = data.ToArray();
    }

    public int HavokOffset { get; }

    public int TimelineOffset { get; }

    /// <summary>The skeleton the pack was authored for, e.g. 101 for c0101.</summary>
    public ushort ModelId => BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(10));

    /// <summary>0 human, 1 monster, 2 demihuman, 3 weapon.</summary>
    public byte ModelType => _bytes[12];

    public ImmutableArray<Entry> Entries { get; }

    public byte[] Havok => _bytes[HavokOffset..TimelineOffset];

    public byte[] ToArray() => _bytes.ToArray();

    /// <summary>The body animations of the pack with their entry indices.</summary>
    public IEnumerable<(Entry Entry, int Index)> BodyEntries
        => Entries.Select((entry, index) => (entry, index)).Where(e => e.entry.IsBody);

    /// <summary>The facial animations of the pack with their entry indices.</summary>
    public IEnumerable<(Entry Entry, int Index)> FaceEntries
        => Entries.Select((entry, index) => (entry, index)).Where(e => !e.entry.IsBody);

    /// <summary>The embedded timeline (a whole TMLB) of entry <paramref name="index"/>.</summary>
    public byte[] Timeline(int index)
    {
        if (index < 0 || index >= Entries.Length) throw new InvalidDataException($"PAP entry {index} does not exist.");
        var offset = TimelineOffset;
        for (var i = 0; ; i++)
        {
            if (offset < 0 || offset > _bytes.Length - 12 || !_bytes.AsSpan(offset, 4).SequenceEqual("TMLB"u8))
                throw new InvalidDataException("Invalid animation timeline header.");
            var length = ReadInt(_bytes, offset + 4);
            if (length < 12 || length > _bytes.Length - offset) throw new InvalidDataException("Invalid animation timeline size.");
            if (i == index) return _bytes[offset..(offset + length)];
            offset += length;
            // Timelines are aligned relative to the first one, not to zero.
            offset += (TimelineOffset - offset) & 3;
        }
    }

    /// <summary>
    /// Adds animations to the pack: their info entries after the existing ones, the Havok
    /// container that now also holds their bindings, and their timelines after the existing
    /// ones. Every existing entry, its bytes and its timeline are kept exactly; only offsets
    /// move. The caller supplies <paramref name="havok"/> with the new bindings appended, so
    /// each new entry's <see cref="Entry.Binding"/> must already point past the old ones.
    /// </summary>
    public byte[] WithAppendedEntries(byte[] havok, IReadOnlyList<(Entry Entry, byte[] Timeline)> appended)
    {
        if (appended.Count == 0) throw new InvalidDataException("No animation to add was supplied.");
        if (havok.Length < 8 || havok.Length > MaxFileSize) throw new InvalidDataException("Invalid Havok container size.");
        var count = Entries.Length + appended.Count;
        if (count > 4096) throw new InvalidDataException("The PAP would hold too many animations.");

        var info = ReadInt(_bytes, InfoOffsetField);
        var stream = new MemoryStream();
        stream.Write(_bytes, 0, info);
        stream.Write(_bytes, info, Entries.Length * EntrySize);
        foreach (var (entry, _) in appended)
        {
            var name = Encoding.ASCII.GetBytes(entry.Name);
            if (!PapTimeline.IsSafeMotionName(entry.Name) || name.Length > NameSize - 1)
                throw new InvalidDataException($"'{entry.Name}' is not a valid animation name.");
            var record = new byte[EntrySize];
            name.CopyTo(record, 0);
            BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(32), entry.Type);
            BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(34), entry.Binding);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(36), entry.Face);
            stream.Write(record);
        }

        // Keep the gap the file had between its entry table and its Havok container.
        var gap = HavokOffset - (info + Entries.Length * EntrySize);
        stream.Write(new byte[Math.Max(0, gap)]);
        var havokOffset = (int)stream.Length;
        stream.Write(havok);

        // The timelines keep the alignment the file used for its first one.
        var pad = (TimelineOffset - (int)stream.Length) & 3;
        stream.Write(new byte[pad]);
        var timelineOffset = (int)stream.Length;
        stream.Write(_bytes, TimelineOffset, _bytes.Length - TimelineOffset);
        foreach (var (_, timeline) in appended)
        {
            if (timeline.Length < 12 || !timeline.AsSpan(0, 4).SequenceEqual("TMLB"u8))
                throw new InvalidDataException("An added animation has no valid timeline.");
            stream.Write(new byte[(timelineOffset - (int)stream.Length) & 3]);
            stream.Write(timeline);
        }

        var result = stream.ToArray();
        if (result.Length > MaxFileSize) throw new InvalidDataException("The rebuilt PAP exceeds the size limit.");
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(8), (short)count);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(HavokOffsetField), havokOffset);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(TimelineOffsetField), timelineOffset);
        _ = new PapFile(result);
        _ = PapTimeline.ReadStrings(result); // every timeline, and nothing after them
        return result;
    }

    /// <summary>Replaces the Havok container, keeping the header, entries and timelines.</summary>
    public byte[] ReplaceHavok(byte[] havok)
    {
        if (havok.Length < 8 || havok.Length > MaxFileSize) throw new InvalidDataException("Invalid Havok container size.");
        // Keep the timelines' original alignment, including nonstandard padding of modded PAPs.
        var padding = (TimelineOffset - (HavokOffset + havok.Length)) & 3;
        var footer = checked(HavokOffset + havok.Length + padding);
        var result = new byte[checked(footer + _bytes.Length - TimelineOffset)];
        if (result.Length > MaxFileSize) throw new InvalidDataException("The rebuilt PAP exceeds the size limit.");
        _bytes.AsSpan(0, HavokOffset).CopyTo(result);
        havok.CopyTo(result, HavokOffset);
        _bytes.AsSpan(TimelineOffset).CopyTo(result.AsSpan(footer));
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(TimelineOffsetField), footer);
        _ = new PapFile(result);
        return result;
    }

    /// <summary>Rewrites the declared skeleton. Every offset is unchanged.</summary>
    public byte[] WithModel(ushort modelId, byte modelType)
    {
        if (modelType > 3) throw new InvalidDataException("Unsupported PAP skeleton type.");
        var result = _bytes.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(10), modelId);
        result[12] = modelType;
        _ = new PapFile(result);
        return result;
    }

    /// <summary>
    /// Renames entries in place, keyed by entry index. The entry count, every offset and all
    /// Havok and timeline bytes are unchanged, so this cannot move a binding.
    /// </summary>
    public byte[] WithEntryNames(IReadOnlyDictionary<int, string> names)
    {
        if (names.Count == 0) throw new InvalidDataException("No PAP entry rename was supplied.");
        var info = ReadInt(_bytes, InfoOffsetField);
        var result = _bytes.ToArray();
        foreach (var (index, name) in names)
        {
            if (index < 0 || index >= Entries.Length) throw new InvalidDataException($"PAP entry {index} does not exist.");
            if (!PapTimeline.IsSafeMotionName(name)) throw new InvalidDataException($"'{name}' is not a valid animation name.");
            var encoded = Encoding.ASCII.GetBytes(name);
            // 32 bytes including the terminator, matching the reader's NUL scan.
            if (encoded.Length > NameSize - 1) throw new InvalidDataException($"The animation name '{name}' is too long.");
            var start = info + index * EntrySize;
            result.AsSpan(start, NameSize).Clear();
            encoded.CopyTo(result.AsSpan(start));
        }
        _ = new PapFile(result);
        return result;
    }

    internal static int ReadInt(byte[] data, int offset)
    {
        if (offset < 0 || offset > data.Length - 4) throw new InvalidDataException("Resource offset is out of bounds.");
        return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset));
    }
}

/// <summary>A string an embedded timeline points at, with the byte position it lives at.</summary>
public readonly record struct TimelineString(string Magic, int Position, string Value, int Field, int Anchor)
{
    /// <summary>C009 and C010 name the animation (PAP entry) the timeline plays.</summary>
    public bool IsMotion => Magic is "C009" or "C010";
}

/// <summary>
/// Reads and renames the motion names of a PAP's embedded timelines. Moving an animation
/// into another slot takes three coordinated changes: the file lands at the destination
/// game path, the PAP entry is renamed, and the embedded timeline's C009/C010 motion name
/// is renamed to match. Miss the last one and the destination timeline plays nothing.
/// </summary>
public static class PapTimeline
{
    public static bool IsSafeMotionName(string name) => name.Length is > 0 and < 256 &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');

    /// <summary>Every string referenced by every embedded timeline, in file order.</summary>
    public static List<TimelineString> ReadStrings(byte[] pap)
    {
        var file = new PapFile(pap);
        var strings = new List<TimelineString>();
        var offset = file.TimelineOffset;
        for (var i = 0; i < file.Entries.Length; i++)
        {
            offset = checked(offset + ReadTimeline(pap, offset, strings));
            if (i + 1 < file.Entries.Length) offset += (file.TimelineOffset - offset) & 3;
        }
        if (offset != pap.Length) throw new InvalidDataException("The PAP has unrecognized bytes after its timelines.");
        return strings;
    }

    /// <summary>The strings of a standalone action timeline (<c>chara/action/*.tmb</c>), a bare TMLB.</summary>
    public static List<TimelineString> ReadActionTimeline(byte[] tmb)
    {
        var strings = new List<TimelineString>();
        if (ReadTimeline(tmb, 0, strings) != tmb.Length)
            throw new InvalidDataException("The action timeline has unrecognized bytes after its entries.");
        return strings;
    }

    /// <summary>
    /// Rewrites motion names inside the embedded timelines.
    /// <para>
    /// A name that fits where the old one lived is written in place. A longer one is appended
    /// to the end of its own timeline and the referencing entry is re-pointed at it. String
    /// displacements are relative to their own entry, so appending never invalidates another
    /// one, and the TMAL/TMAC/TMTR byte counts end before the string area. Only the TMLB
    /// length covers the whole timeline, and it is rewritten here. Swapping a numbered idle
    /// with its family's base member needs this: <c>jmn</c> and <c>cbem_pose03_2lp</c> are
    /// nothing like the same length.
    /// </para>
    /// </summary>
    public static byte[] RenameMotions(byte[] pap, IReadOnlyDictionary<string, string> renames)
    {
        if (renames.Count == 0) throw new InvalidDataException("No timeline motion rename was supplied.");
        foreach (var (from, to) in renames)
            if (from.Length == 0 || !IsSafeMotionName(to))
                throw new InvalidDataException($"'{from}' → '{to}' is not a valid timeline motion rename.");

        var file = new PapFile(pap);
        var footer = new MemoryStream();
        var replaced = 0;
        var offset = file.TimelineOffset;
        for (var i = 0; i < file.Entries.Length; i++)
        {
            var sites = new List<TimelineString>();
            var length = ReadTimeline(pap, offset, sites);
            var timeline = new MemoryStream();
            timeline.Write(pap, offset, length);
            foreach (var site in sites)
            {
                if (!site.IsMotion || !renames.TryGetValue(site.Value, out var name)) continue;
                replaced++;
                var local = site.Position - offset;
                if (name.Length <= site.Value.Length)
                {
                    var buffer = timeline.GetBuffer();
                    for (var c = 0; c < name.Length; c++) buffer[local + c] = (byte)name[c];
                    buffer[local + name.Length] = 0;
                    continue;
                }
                var appended = (int)timeline.Length;
                timeline.Write(Encoding.ASCII.GetBytes(name));
                timeline.WriteByte(0);
                BinaryPrimitives.WriteInt32LittleEndian(timeline.GetBuffer().AsSpan(site.Field - offset),
                    appended - (site.Anchor - offset));
            }

            var bytes = timeline.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length);
            footer.Write(bytes);
            offset = checked(offset + length);
            if (i + 1 < file.Entries.Length)
            {
                // Timelines are aligned relative to the first one, not to zero.
                offset += (file.TimelineOffset - offset) & 3;
                footer.Write(new byte[(int)(-footer.Length & 3)]);
            }
        }
        if (replaced == 0) throw new InvalidDataException("The animation's timeline does not reference the motion being renamed.");

        var result = new byte[checked(file.TimelineOffset + (int)footer.Length)];
        if (result.Length > PapFile.MaxFileSize) throw new InvalidDataException("The renamed PAP exceeds the size limit.");
        pap.AsSpan(0, file.TimelineOffset).CopyTo(result);
        footer.ToArray().CopyTo(result, file.TimelineOffset);
        Verify(pap, result, renames);
        return result;
    }

    /// <summary>
    /// Re-reads the result and requires that it differs from the input only by the rename.
    /// This turns "displacements are entry-relative" from an assumption into a checked
    /// post-condition, and catches padding or length headers that no longer add up.
    /// </summary>
    private static void Verify(byte[] before, byte[] after, IReadOnlyDictionary<string, string> renames)
    {
        var expected = ReadStrings(before)
            .Select(s => (s.Magic, Value: s.IsMotion && renames.TryGetValue(s.Value, out var name) ? name : s.Value))
            .ToList();
        var actual = ReadStrings(after).Select(s => (s.Magic, s.Value)).ToList();
        if (!expected.SequenceEqual(actual))
            throw new InvalidDataException("Renaming the animation's timeline changed more than the motion name.");
    }

    private static int ReadTimeline(byte[] bytes, int start, List<TimelineString> strings)
    {
        if (start < 0 || start > bytes.Length - 12 || !bytes.AsSpan(start, 4).SequenceEqual("TMLB"u8))
            throw new InvalidDataException("Invalid animation timeline header.");
        var length = PapFile.ReadInt(bytes, start + 4);
        var entries = PapFile.ReadInt(bytes, start + 8);
        if (length < 12 || length > bytes.Length - start || entries is < 0 or > 65536)
            throw new InvalidDataException("Invalid animation timeline size.");
        var end = start + length;
        var cursor = start + 12;
        for (var i = 0; i < entries; i++)
        {
            if (cursor > end - 8) throw new InvalidDataException("Truncated animation timeline entry.");
            var magic = Encoding.ASCII.GetString(bytes, cursor, 4);
            var size = PapFile.ReadInt(bytes, cursor + 4);
            if (size < 8 || size > end - cursor) throw new InvalidDataException($"Invalid {magic} timeline entry size.");
            // Offset of the string displacement within the entry, from VFXEditor's TMB layouts.
            var field = magic switch
            {
                "C002" => 24,
                "C009" => 20,
                "C010" => 32,
                "C012" or "C063" or "C173" => 20,
                _ => -1,
            };
            if (field >= 0 && field <= size - 4)
            {
                var displacement = PapFile.ReadInt(bytes, cursor + field);
                if (displacement != 0)
                {
                    var position = (long)cursor + 8 + displacement;
                    if (position < start || position >= end) throw new InvalidDataException($"Invalid {magic} string offset.");
                    var value = ReadString(bytes, (int)position, end);
                    if (value.Length > 0) strings.Add(new TimelineString(magic, (int)position, value, cursor + field, cursor + 8));
                }
            }
            cursor += size;
        }
        return length;
    }

    private static string ReadString(byte[] bytes, int start, int end)
    {
        var span = bytes.AsSpan(start, Math.Min(512, end - start));
        var zero = span.IndexOf((byte)0);
        if (zero < 0) throw new InvalidDataException("Unterminated animation timeline string.");
        return Encoding.UTF8.GetString(span[..zero]);
    }
}
