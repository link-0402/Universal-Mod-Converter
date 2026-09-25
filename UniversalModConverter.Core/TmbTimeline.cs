using System.Buffers.Binary;
using System.Text;

namespace UniversalModConverter.Core;

/// <summary>
/// One timeline (TMLB), taken apart so entries can be added and strings changed. A timeline is
/// a run of fixed-size items (header, actors, tracks, entries) followed by three areas the
/// items point into: extra data, track id lists and strings, each pointer relative to its own
/// item. Written back in the layout VFXEditor and the game use (items, extra data, lists, then
/// strings once each, all in item order), so an unedited timeline comes back byte for byte and
/// nothing is left behind that a reader would flag, such as a string no entry names any more.
/// Layouts follow VFXEditor's TmbFormat.
/// </summary>
public sealed class TmbTimeline
{
    private const int HeaderSize = 12;

    private enum Kind
    {
        /// <summary>A NUL-terminated string.</summary>
        String,
        /// <summary>Item ids (shorts), counted by the int after the pointer.</summary>
        List,
        /// <summary>Floats, counted by the int after the pointer.</summary>
        Vector,
        /// <summary>A track's condition: 8 bytes, then 12 per condition counted at +4.</summary>
        Condition,
        /// <summary>C094's filter block, 0x14 bytes.</summary>
        Filter,
        /// <summary>TMFC's curves, from this pointer to the end pointer at +24, both relative to the item + 12.</summary>
        Curves,
    }

    private readonly record struct Layout(int Offset, Kind Kind, int Base = 8);

    /// <summary>The pointers of every item that has any.</summary>
    private static readonly Dictionary<string, Layout[]> Pointers = new(StringComparer.Ordinal)
    {
        ["TMPP"] = [new(8, Kind.String)],
        ["TMAL"] = [new(8, Kind.List)],
        ["TMAC"] = [new(20, Kind.List)],
        ["TMTR"] = [new(12, Kind.List), new(20, Kind.Condition)],
        ["TMFC"] = [new(12, Kind.Curves, 12)],
        ["C002"] = [new(24, Kind.String)],
        ["C009"] = [new(20, Kind.String)],
        ["C010"] = [new(32, Kind.String)],
        ["C012"] = [new(20, Kind.String), new(32, Kind.Vector), new(40, Kind.Vector), new(48, Kind.Vector), new(56, Kind.Vector)],
        ["C063"] = [new(20, Kind.String)],
        ["C068"] = [new(20, Kind.Vector), new(28, Kind.Vector)],
        ["C075"] = [new(24, Kind.Vector), new(32, Kind.Vector), new(40, Kind.Vector), new(48, Kind.Vector)],
        ["C093"] = [new(20, Kind.Vector), new(28, Kind.Vector)],
        ["C094"] = [new(28, Kind.Filter)],
        ["C173"] = [new(20, Kind.String)],
    };

    /// <summary>Items known to hold no pointers.</summary>
    private static readonly HashSet<string> Plain = new(StringComparer.Ordinal)
    {
        "TMDH", "C006", "C011", "C013", "C014", "C015", "C021", "C031", "C033", "C034", "C042", "C043", "C053",
        "C067", "C083", "C084", "C088", "C089", "C095", "C100", "C107", "C117", "C118", "C120", "C124", "C125",
        "C131", "C136", "C139", "C142", "C143", "C144", "C161", "C168", "C174", "C175", "C176", "C177", "C178",
        "C187", "C188", "C192", "C194", "C197", "C198", "C199", "C202", "C203", "C204", "C211", "C212", "C215",
        "C216", "C225", "C230", "C234",
    };

    private sealed class Field(Layout layout)
    {
        public Layout Layout { get; } = layout;
        /// <summary>False for a pointer the file left at zero, which stays zero.</summary>
        public bool Present { get; set; }
        public string Text { get; set; } = string.Empty;
        public List<short> Ids { get; } = [];
        public byte[] Data { get; set; } = [];
    }

    private sealed class Item(string magic, byte[] bytes)
    {
        public string Magic { get; } = magic;
        public byte[] Bytes { get; } = bytes;
        public List<Field> Fields { get; } = [];

        /// <summary>Actors, tracks and entries carry an id; the header items do not.</summary>
        public bool HasId => Magic is "TMAC" or "TMTR" or "TMFC" || Magic.StartsWith('C');

        public short Id => BinaryPrimitives.ReadInt16LittleEndian(Bytes.AsSpan(8));

        public Field? String => Fields.FirstOrDefault(f => f.Layout.Kind == Kind.String);
    }

    private readonly List<Item> _items;

    private TmbTimeline(List<Item> items) => _items = items;

    /// <summary>
    /// Reads the timeline at <paramref name="start"/>, which runs for the length its header gives.
    /// Throws <see cref="InvalidDataException"/> for a damaged timeline or one with an entry
    /// whose layout is unknown, since moving it could break a pointer it holds.
    /// </summary>
    public static TmbTimeline Parse(byte[] bytes, int start = 0)
    {
        if (start < 0 || start > bytes.Length - HeaderSize || !bytes.AsSpan(start, 4).SequenceEqual("TMLB"u8))
            throw new InvalidDataException("Invalid animation timeline header.");
        var length = PapFile.ReadInt(bytes, start + 4);
        var count = PapFile.ReadInt(bytes, start + 8);
        if (length < HeaderSize || length > bytes.Length - start || count is < 0 or > 65536)
            throw new InvalidDataException("Invalid animation timeline size.");
        var end = start + length;

        var items = new List<(Item Item, int Position)>();
        var cursor = start + HeaderSize;
        for (var i = 0; i < count; i++)
        {
            if (cursor > end - 8) throw new InvalidDataException("Truncated animation timeline entry.");
            var magic = Encoding.ASCII.GetString(bytes, cursor, 4);
            var size = PapFile.ReadInt(bytes, cursor + 4);
            if (size < 8 || size > end - cursor) throw new InvalidDataException($"Invalid {magic} timeline entry size.");
            if (!Pointers.ContainsKey(magic) && !Plain.Contains(magic))
                throw new InvalidDataException($"The animation's timeline has an entry of a kind this converter does not know ({magic}).");
            items.Add((new Item(magic, bytes[cursor..(cursor + size)]), cursor));
            cursor += size;
        }

        // Every pointer leads past the items, into the data areas.
        var data = cursor;
        foreach (var (item, position) in items)
        {
            if (!Pointers.TryGetValue(item.Magic, out var layouts)) continue;
            foreach (var layout in layouts)
            {
                var field = new Field(layout);
                item.Fields.Add(field);
                var counted = layout.Kind is Kind.List or Kind.Vector;
                if (layout.Offset > item.Bytes.Length - (counted || layout.Kind == Kind.Curves ? 8 : 4))
                    throw new InvalidDataException($"The {item.Magic} timeline entry is too short.");
                var pointer = BinaryPrimitives.ReadInt32LittleEndian(item.Bytes.AsSpan(layout.Offset));
                var number = counted ? BinaryPrimitives.ReadInt32LittleEndian(item.Bytes.AsSpan(layout.Offset + 4)) : 0;
                if (layout.Kind == Kind.Curves && item.Bytes.Length < 28)
                    throw new InvalidDataException("The TMFC timeline entry is too short.");
                if (pointer == 0 && (layout.Kind != Kind.List || number == 0)) continue;

                var target = (long)position + layout.Base + pointer;
                if (target < data || target > end) throw new InvalidDataException($"Invalid {item.Magic} timeline pointer.");
                var at = (int)target;
                field.Present = true;
                switch (layout.Kind)
                {
                    case Kind.String:
                        field.Text = ReadString(bytes, at, end);
                        break;
                    case Kind.List:
                        if (number is < 0 or > 4096 || at > end - number * 2)
                            throw new InvalidDataException($"Invalid {item.Magic} timeline id list.");
                        for (var n = 0; n < number; n++) field.Ids.Add(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(at + n * 2)));
                        break;
                    case Kind.Vector:
                        if (number is < 1 or > 16) throw new InvalidDataException($"Invalid {item.Magic} timeline vector.");
                        field.Data = Slice(bytes, at, number * 4, end);
                        break;
                    case Kind.Condition:
                        var conditions = at > end - 8 ? -1 : PapFile.ReadInt(bytes, at + 4);
                        if (conditions is < 0 or > 1024) throw new InvalidDataException("Invalid timeline track condition.");
                        field.Data = Slice(bytes, at, 8 + 12 * conditions, end);
                        break;
                    case Kind.Filter:
                        field.Data = Slice(bytes, at, 0x14, end);
                        break;
                    case Kind.Curves:
                        var last = (long)position + layout.Base + BinaryPrimitives.ReadInt32LittleEndian(item.Bytes.AsSpan(24));
                        if (last < at || last > end) throw new InvalidDataException("Invalid TMFC timeline curves.");
                        field.Data = bytes[at..(int)last];
                        break;
                }
            }
        }
        return new TmbTimeline(items.Select(i => i.Item).ToList());
    }

    /// <summary>The motion names the timeline plays (C009 and C010), in item order.</summary>
    public IEnumerable<string> Motions
        => _items.Where(i => i.Magic is "C009" or "C010" && i.String is { Present: true }).Select(i => i.String!.Text);

    /// <summary>The facial animations the timeline plays (C010 names starting with <c>cfx</c>).</summary>
    public IEnumerable<string> Faces
        => _items.Where(i => i.Magic == "C010" && i.String is { Present: true } s && s.Text.StartsWith("cfx", StringComparison.Ordinal))
            .Select(i => i.String!.Text);

    /// <summary>
    /// The face pack (TMPP) the timeline has the game load, by name: <c>smile</c> for the
    /// character's <c>f000x/nonresident/smile.pap</c>. Null when it asks for none.
    /// </summary>
    public string? FacePack
    {
        get => _items.FirstOrDefault(i => i.Magic == "TMPP")?.String is { Present: true } s ? s.Text : null;
        set
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (!PapTimeline.IsSafeMotionName(value)) throw new InvalidDataException($"'{value}' is not a valid face pack name.");
            var pack = _items.FirstOrDefault(i => i.Magic == "TMPP");
            if (pack == null)
            {
                pack = new Item("TMPP", Header("TMPP", 12));
                pack.Fields.Add(new Field(Pointers["TMPP"][0]));
                // Right after the TMDH header, where the game and VFXEditor keep it.
                var header = _items.FindIndex(i => i.Magic == "TMDH");
                _items.Insert(header + 1, pack);
            }
            pack.String!.Present = true;
            pack.String.Text = value;
        }
    }

    /// <summary>Renames the motions of C009 and C010 entries; returns how many it changed.</summary>
    public int RenameMotions(IReadOnlyDictionary<string, string> renames)
    {
        var renamed = 0;
        foreach (var item in _items.Where(i => i.Magic is "C009" or "C010"))
        {
            if (item.String is not { Present: true } field || !renames.TryGetValue(field.Text, out var name)) continue;
            if (!PapTimeline.IsSafeMotionName(name)) throw new InvalidDataException($"'{name}' is not a valid animation name.");
            field.Text = name;
            renamed++;
        }
        return renamed;
    }

    /// <summary>How many frames the C009 or C010 playing <paramref name="motion"/> lasts, or null when none does.</summary>
    public int? DurationOf(string motion)
        => Playing(motion) is { } item ? BinaryPrimitives.ReadInt32LittleEndian(item.Bytes.AsSpan(12)) : null;

    /// <summary>How the timeline plays the facial animation <paramref name="face"/>, or null when it does not.</summary>
    public FaceTiming? TimingOf(string face)
    {
        if (_items.FirstOrDefault(i => i.Magic == "C010" && i.String is { Present: true } s && s.Text == face) is not { } item ||
            item.Bytes.Length < 40)
            return null;
        var span = item.Bytes.AsSpan();
        return new FaceTiming(BinaryPrimitives.ReadInt32LittleEndian(span[12..]), BinaryPrimitives.ReadInt32LittleEndian(span[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(span[20..]), BinaryPrimitives.ReadSingleLittleEndian(span[24..]),
            BinaryPrimitives.ReadSingleLittleEndian(span[28..]), BinaryPrimitives.ReadInt32LittleEndian(span[36..]));
    }

    private Item? Playing(string motion)
        => _items.FirstOrDefault(i => i.Magic is "C009" or "C010" && i.String is { Present: true } s && s.Text == motion);

    /// <summary>
    /// Has the timeline play the facial animation <paramref name="face"/> alongside
    /// <paramref name="bodyMotion"/>: a C010 on the body animation's track, starting with it.
    /// It plays as <paramref name="timing"/> says; without one it holds the face's first frame
    /// for as long as the body lasts, as the game's own do for single-frame poses.
    /// </summary>
    public void AddFace(string bodyMotion, string face, FaceTiming? timing = null)
    {
        if (!PapTimeline.IsSafeMotionName(face)) throw new InvalidDataException($"'{face}' is not a valid animation name.");
        var body = Playing(bodyMotion) ?? throw new InvalidDataException($"The animation's timeline never plays '{bodyMotion}'.");
        var track = _items.FirstOrDefault(i => i.Magic == "TMTR" && i.Fields[0].Ids.Contains(body.Id))
                    ?? throw new InvalidDataException($"The animation's timeline plays '{bodyMotion}' on no track.");

        var id = (short)(_items.Where(i => i.HasId).Select(i => (int)i.Id).DefaultIfEmpty(1).Max() + 1);
        var bytes = Header("C010", 0x28);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(8), id);
        body.Bytes.AsSpan(10, 2).CopyTo(bytes.AsSpan(10));                  // starts with the body
        if (timing == null)
        {
            body.Bytes.AsSpan(12, 4).CopyTo(bytes.AsSpan(12));              // and lasts as long
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), 1);   // time control on
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(24), 0f); // from the face's first frame
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(28), 1f); // to its second
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), timing.Duration);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), timing.Unknown1);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), timing.Flags);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(24), timing.Start);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(28), timing.End);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(36), timing.Unknown2);
        }
        var entry = new Item("C010", bytes);
        entry.Fields.Add(new Field(Pointers["C010"][0]) { Present = true, Text = face });

        _items.Add(entry);
        track.Fields[0].Ids.Add(id);
        track.Fields[0].Present = true;
    }

    /// <summary>The timeline in the game's layout.</summary>
    public byte[] ToArray()
    {
        var itemsLength = _items.Sum(i => i.Bytes.Length);
        var extraLength = _items.SelectMany(i => i.Fields).Where(f => f.Present && IsExtra(f.Layout.Kind)).Sum(f => f.Data.Length);
        var listLength = _items.SelectMany(i => i.Fields).Where(f => f.Layout.Kind == Kind.List).Sum(f => f.Ids.Count * 2);
        var extraStart = HeaderSize + itemsLength;
        var listStart = extraStart + extraLength;
        var stringStart = listStart + listLength;

        var body = new MemoryStream();
        var extra = new MemoryStream();
        var lists = new MemoryStream();
        var strings = new MemoryStream();
        var written = new Dictionary<string, int>(StringComparer.Ordinal);
        var position = HeaderSize;
        foreach (var item in _items)
        {
            var bytes = item.Bytes.ToArray();
            foreach (var field in item.Fields)
            {
                var from = position + field.Layout.Base;
                var at = field.Layout.Offset;
                if (!field.Present && field.Layout.Kind != Kind.List)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), 0);
                    continue;
                }
                switch (field.Layout.Kind)
                {
                    case Kind.String:
                        if (!written.TryGetValue(field.Text, out var offset))
                        {
                            offset = (int)strings.Length;
                            written[field.Text] = offset;
                            strings.Write(Encoding.UTF8.GetBytes(field.Text));
                            strings.WriteByte(0);
                        }
                        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), stringStart + offset - from);
                        break;
                    case Kind.List:
                        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), listStart + (int)lists.Length - from);
                        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at + 4), field.Ids.Count);
                        foreach (var id in field.Ids)
                        {
                            lists.WriteByte((byte)id);
                            lists.WriteByte((byte)(id >> 8));
                        }
                        break;
                    default:
                        var pointer = extraStart + (int)extra.Length - from;
                        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), pointer);
                        if (field.Layout.Kind == Kind.Curves)
                            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), pointer + field.Data.Length);
                        extra.Write(field.Data);
                        break;
                }
            }
            body.Write(bytes);
            position += bytes.Length;
        }

        var result = new MemoryStream();
        result.Write("TMLB"u8);
        result.Write(new byte[8]);
        body.WriteTo(result);
        extra.WriteTo(result);
        lists.WriteTo(result);
        strings.WriteTo(result);
        var output = result.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), output.Length);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(8), _items.Count);
        return output;
    }

    private static bool IsExtra(Kind kind) => kind is Kind.Vector or Kind.Condition or Kind.Filter or Kind.Curves;

    private static byte[] Header(string magic, int size)
    {
        var bytes = new byte[size];
        Encoding.ASCII.GetBytes(magic).CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), size);
        return bytes;
    }

    private static byte[] Slice(byte[] bytes, int start, int length, int end)
    {
        if (length < 0 || start > end - length) throw new InvalidDataException("A timeline pointer runs past the timeline.");
        return bytes[start..(start + length)];
    }

    private static string ReadString(byte[] bytes, int start, int end)
    {
        var span = bytes.AsSpan(start, Math.Min(512, end - start));
        var zero = span.IndexOf((byte)0);
        if (zero < 0) throw new InvalidDataException("Unterminated animation timeline string.");
        return Encoding.UTF8.GetString(span[..zero]);
    }
}
