using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace UniversalModConverter.Core;

/// <summary>
/// The game's PAP load table, <c>chara/xls/animation/papLoadTable.plt</c>: for every race and
/// animation set, whose copy of each body animation the game loads. Most races play another
/// race's copy of most emotes, and not along the skeleton parents: Miqo'te Female plays the
/// Midlander Male's <c>emote/dance12_loop</c>. The game never asks for a borrowing race's own path,
/// even where it has a file there, so a mod's file there never loads (measured 2026-11-05).
/// <para>
/// Layout, little endian. Header: u16 entry count, u16 record count, u32 unused here, u32 offset of
/// the folder names, u32 offset of the animation names. Entries, 12 bytes: u32 flags, u32 folder
/// name, u32 animation name. Records, 168 bytes: u32 owner (race, with a kind above it, 0 for
/// humans), u16 set, u16 first remap group, then one bit per group of four entries; a set bit sends
/// the group to the remaps, in bit order. Remaps, 8 bytes, fill the space up to the folder names:
/// u32 owner, u16 set, u16 folder name.
/// </para>
/// </summary>
public sealed class PapLoadTable
{
    public const string FilePath = "chara/xls/animation/papLoadTable.plt";

    private const int HeaderSize = 16, EntrySize = 12, RecordSize = 168, RemapSize = 8, RecordBits = 8;
    private const int GroupsPerRecord = (RecordSize - RecordBits) * 8;
    private const int EntriesPerGroup = 4;

    private readonly byte[] _file;
    private readonly int _remaps, _folders;
    private readonly Dictionary<(string Folder, string Name), int> _entries = [];
    private readonly Dictionary<(uint Owner, ushort Set), int> _records = [];

    /// <exception cref="InvalidDataException">The file is not laid out as the game's is.</exception>
    public PapLoadTable(byte[] file)
    {
        _file = file;
        if (file.Length < HeaderSize) throw new InvalidDataException("The file is shorter than its header.");
        var entryCount = BinaryPrimitives.ReadUInt16LittleEndian(file);
        var recordCount = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(2));
        var folders = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));
        var names = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(12));
        var records = HeaderSize + entryCount * EntrySize;
        var remaps = records + (long)recordCount * RecordSize;
        if (remaps > folders || folders >= names || names > file.Length)
            throw new InvalidDataException("Its sections overlap or run past its end.");
        _remaps = (int)remaps;
        _folders = (int)folders;
        var remapCount = (folders - remaps) / RemapSize;

        for (var i = 0; i < entryCount; i++)
        {
            var at = HeaderSize + i * EntrySize;
            // The game lists one mount animation twice: a lookup by name finds the first.
            _entries.TryAdd((Text(_folders, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4))),
                Text((int)names, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 8)))), i);
        }

        for (var i = 0; i < recordCount; i++)
        {
            var at = records + i * RecordSize;
            var slot = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 6)) * (long)EntriesPerGroup;
            for (var group = 0; group < GroupsPerRecord; group++)
            {
                if (!Bit(at, group)) continue;
                if (slot + EntriesPerGroup > remapCount) throw new InvalidDataException("A record runs past the remaps.");
                // The last group is padded to four: slots past the last entry name no folder and are never read.
                for (var entry = 0; entry < EntriesPerGroup && group * EntriesPerGroup + entry < entryCount; entry++)
                    if (BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(_remaps + (int)(slot + entry) * RemapSize + 6)) >= names - folders)
                        throw new InvalidDataException("A remap names a folder past the folder names.");
                slot += EntriesPerGroup;
            }
            _records.TryAdd((BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at)), BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 4))), at);
        }
    }

    /// <summary>
    /// The table the game uses, or null when it has none to give or it cannot be read, which
    /// <paramref name="problem"/> then says.
    /// </summary>
    public static PapLoadTable? TryRead(IGameFileProvider game, out string? problem)
    {
        problem = null;
        try
        {
            return game.ReadFile(FilePath) is { } file ? new PapLoadTable(file) : null;
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// The file the game loads when <paramref name="path"/>'s race asks for it: another race's when
    /// the table borrows one, else <paramref name="path"/> itself. Only body animations are listed.
    /// </summary>
    public PapPath Resolve(PapPath path)
    {
        if (path.IsFacial || !ushort.TryParse(path.Set.AsSpan(1), out var set) ||
            !_entries.TryGetValue((path.Directory, path.Key), out var entry) ||
            !_records.TryGetValue((path.Race, set), out var record))
            return path;

        var group = entry / EntriesPerGroup;
        if (group >= GroupsPerRecord || !Bit(record, group)) return path;
        var rank = 0;
        for (var earlier = 0; earlier < group / 8; earlier++) rank += BitOperations.PopCount(_file[record + RecordBits + earlier]);
        rank += BitOperations.PopCount((uint)(_file[record + RecordBits + group / 8] & ((1 << group % 8) - 1)));

        var first = BinaryPrimitives.ReadUInt16LittleEndian(_file.AsSpan(record + 6));
        var at = _remaps + ((first + rank) * EntriesPerGroup + entry % EntriesPerGroup) * RemapSize;
        var owner = BinaryPrimitives.ReadUInt32LittleEndian(_file.AsSpan(at));
        // Humans only borrow from humans; any other kind has a path layout of its own.
        if (owner >> 16 != 0 || owner > 9999) return path;
        return new PapPath((ushort)owner, $"a{BinaryPrimitives.ReadUInt16LittleEndian(_file.AsSpan(at + 4)):D4}",
            Text(_folders, BinaryPrimitives.ReadUInt16LittleEndian(_file.AsSpan(at + 6))), path.Key);
    }

    private bool Bit(int record, int group) => (_file[record + RecordBits + group / 8] >> group % 8 & 1) != 0;

    private string Text(int table, uint offset)
    {
        var start = table + (long)offset;
        if (start >= _file.Length) throw new InvalidDataException("A name runs past the end of the file.");
        var length = _file.AsSpan((int)start).IndexOf((byte)0);
        if (length < 0) throw new InvalidDataException("A name has no terminator.");
        return Encoding.ASCII.GetString(_file, (int)start, length);
    }
}
