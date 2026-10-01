using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace UniversalModConverter.Core;

/// <summary>
/// An FFXIV animation pack (.pap): a packed header, one info entry per animation, the
/// Havok animation container and one embedded timeline (TMLB) per animation. Layout
/// follows VFXEditor's PapFile/PapAnimation. Everything not explicitly edited, including
/// the Havok and timeline bytes, is preserved byte for byte.
/// </summary>
public sealed class PapFile
{
    /// <summary>
    /// One animation of the pack. The entry's face flag is not read: the game sets it on facial
    /// animations and on some body animations alike, so it does not tell them apart.
    /// </summary>
    public sealed record Entry(string Name, short Type, short Binding)
    {
        /// <summary>
        /// Facial animations (<c>cfxf_</c> expressions, <c>cfxb_</c> blinks, <c>cfxl_</c> lips)
        /// play on the face skeleton and only live in the face packs a body timeline names.
        /// </summary>
        public bool IsFacial => Type is 17 or 18 or 19 || Name.StartsWith("cfx", StringComparison.Ordinal);

        public bool IsBody => !IsFacial;
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

    /// <summary>
    /// Reads <paramref name="data"/> in place rather than copying it: packs run to megabytes and
    /// are read several times per conversion. Nothing here writes to the array, since every edit
    /// returns a new one, and the caller must not change it afterwards either.
    /// </summary>
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
                BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(start + 32)), binding));
        }

        Entries = entries.MoveToImmutable();
        _bytes = data;
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
    /// Replaces the embedded timelines, one per entry, keeping everything before them. They are
    /// aligned relative to the first, as <see cref="Timeline"/> reads them; the last is not padded.
    /// </summary>
    public byte[] WithTimelines(IReadOnlyList<byte[]> timelines)
    {
        if (timelines.Count != Entries.Length) throw new InvalidDataException("Every animation needs exactly one timeline.");
        var footer = new MemoryStream();
        for (var i = 0; i < timelines.Count; i++)
        {
            if (timelines[i].Length < 12 || !timelines[i].AsSpan(0, 4).SequenceEqual("TMLB"u8))
                throw new InvalidDataException("An animation has no valid timeline.");
            footer.Write(timelines[i]);
            if (i + 1 < timelines.Count) footer.Write(new byte[(int)(-footer.Length & 3)]);
        }

        var result = new byte[checked(TimelineOffset + (int)footer.Length)];
        if (result.Length > MaxFileSize) throw new InvalidDataException("The rebuilt PAP exceeds the size limit.");
        _bytes.AsSpan(0, TimelineOffset).CopyTo(result);
        footer.GetBuffer().AsSpan(0, (int)footer.Length).CopyTo(result.AsSpan(TimelineOffset));
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

    /// <summary>
    /// A face pack's name: motion names, one per folder level (<c>emot/upset</c>, <c>resident/face</c>),
    /// none of them empty or a dot that would step out of the folder.
    /// </summary>
    public static bool IsSafePackName(string name) => name.Length is > 0 and < 256 &&
        name.Split('/').All(part => part is not ("." or "..") && IsSafeMotionName(part));

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
    /// Each timeline is rebuilt with its new names by <see cref="TmbTimeline"/>, in the layout
    /// the game and VFXEditor use, so no old name is left behind. Swapping a numbered idle with
    /// its family's base member changes the name's length a lot: <c>jmn</c> and
    /// <c>cbem_pose03_2lp</c> are nothing like the same length.
    /// </para>
    /// <para>
    /// A timeline that cannot be taken apart (damaged, or with an entry of an unknown layout) is
    /// patched instead: a name that fits is written over the old one, a longer one is appended
    /// to the end of the timeline and its entry re-pointed there. String displacements are
    /// relative to their own entry, so this never moves another one.
    /// </para>
    /// </summary>
    public static byte[] RenameMotions(byte[] pap, IReadOnlyDictionary<string, string> renames)
    {
        if (renames.Count == 0) throw new InvalidDataException("No timeline motion rename was supplied.");
        foreach (var (from, to) in renames)
            if (from.Length == 0 || !IsSafeMotionName(to))
                throw new InvalidDataException($"'{from}' → '{to}' is not a valid timeline motion rename.");

        var file = new PapFile(pap);
        var timelines = new List<byte[]>();
        var replaced = 0;
        for (var i = 0; i < file.Entries.Length; i++)
        {
            var original = file.Timeline(i);
            TmbTimeline? parsed;
            try
            {
                parsed = TmbTimeline.Parse(original);
            }
            catch (InvalidDataException)
            {
                parsed = null;
            }

            if (parsed != null)
            {
                var count = parsed.RenameMotions(renames);
                replaced += count;
                timelines.Add(count == 0 ? original : parsed.ToArray());
            }
            else
            {
                timelines.Add(PatchMotions(original, renames, out var count));
                replaced += count;
            }
        }
        if (replaced == 0) throw new InvalidDataException("The animation's timeline does not reference the motion being renamed.");

        var result = file.WithTimelines(timelines);
        Verify(pap, result, renames);
        return result;
    }

    /// <summary>Renames motions in place in a timeline <see cref="TmbTimeline"/> cannot read.</summary>
    private static byte[] PatchMotions(byte[] original, IReadOnlyDictionary<string, string> renames, out int replaced)
    {
        replaced = 0;
        var sites = new List<TimelineString>();
        ReadTimeline(original, 0, sites);
        var timeline = new MemoryStream();
        timeline.Write(original);
        foreach (var site in sites)
        {
            if (!site.IsMotion || !renames.TryGetValue(site.Value, out var name)) continue;
            replaced++;
            if (name.Length <= site.Value.Length)
            {
                var buffer = timeline.GetBuffer();
                for (var c = 0; c < name.Length; c++) buffer[site.Position + c] = (byte)name[c];
                buffer[site.Position + name.Length] = 0;
                continue;
            }
            var appended = (int)timeline.Length;
            timeline.Write(Encoding.ASCII.GetBytes(name));
            timeline.WriteByte(0);
            BinaryPrimitives.WriteInt32LittleEndian(timeline.GetBuffer().AsSpan(site.Field), appended - site.Anchor);
        }

        var bytes = timeline.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length);
        return bytes;
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
            // The same layouts TmbTimeline edits with, so a check reads every string an edit may touch.
            var field = TmbTimeline.StringField(magic);
            if (field >= 0 && field <= size - 4)
            {
                var displacement = PapFile.ReadInt(bytes, cursor + field);
                if (displacement != 0)
                {
                    var position = (long)cursor + 8 + displacement;
                    if (position < start || position >= end) throw new InvalidDataException($"Invalid {magic} string offset.");
                    var value = TmbTimeline.ReadString(bytes, (int)position, end);
                    if (value.Length > 0) strings.Add(new TimelineString(magic, (int)position, value, cursor + field, cursor + 8));
                }
            }
            cursor += size;
        }
        return length;
    }
}
