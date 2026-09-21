using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Text;

namespace AdvancedPenumbraModConverter.Core;

public enum MdlVertexType : byte
{
    Single1 = 0, Single2 = 1, Single3 = 2, Single4 = 3,
    Unknown4 = 4, UByte4 = 5, Short2 = 6, Short4 = 7,
    NByte4 = 8, NShort2 = 9, NShort4 = 10,
    Unknown11 = 11, Unknown12 = 12, Half2 = 13, Half4 = 14,
    Unknown15 = 15, UShort2 = 16, UShort4 = 17,
}

public enum MdlVertexUsage : byte
{
    Position = 0, BlendWeights = 1, BlendIndices = 2, Normal = 3,
    Uv = 4, Flow = 5, Tangent = 6, Colour = 7,
}

public sealed record MdlVertexElement(byte Stream, byte Offset, MdlVertexType Type,
    MdlVertexUsage Usage, byte UsageIndex, ImmutableArray<byte> Reserved);

public sealed record MdlVertexDeclaration(ImmutableArray<MdlVertexElement> Elements,
    ImmutableArray<byte> ReservedBytes);

public sealed record MdlFileHeader(uint Version, uint StackSize, uint RuntimeSize,
    ushort VertexDeclarationCount, ushort MaterialCount, ImmutableArray<uint> VertexOffsets,
    ImmutableArray<uint> IndexOffsets, ImmutableArray<uint> VertexBufferSizes,
    ImmutableArray<uint> IndexBufferSizes, byte LodCount, bool IndexBufferStreaming,
    bool EdgeGeometryEnabled, byte Reserved);

public sealed record MdlModelHeader(float Radius, ushort MeshCount, ushort AttributeCount,
    ushort SubmeshCount, ushort MaterialCount, ushort BoneCount, ushort BoneTableCount,
    ushort ShapeCount, ushort ShapeMeshCount, ushort ShapeValueCount, byte LodCount,
    byte Flags1, ushort ElementIdCount, byte TerrainShadowMeshCount, byte Flags2,
    float ModelClipDistance, float ShadowClipDistance, ushort CullingGridCount,
    ushort TerrainShadowSubmeshCount, byte Flags3, byte BackgroundMaterialIndex,
    byte CrestMaterialIndex, byte NeckMorphCount, ushort BoneTableArrayCount,
    ushort Unknown, uint FaceDataCount, ImmutableArray<byte> Reserved);

public sealed record MdlElementId(uint Id, uint ParentBone, Vector3 Translation, Vector3 Rotation);

public sealed record MdlLod(ushort MeshIndex, ushort MeshCount, float ModelRange, float TextureRange,
    ushort WaterMeshIndex, ushort WaterMeshCount, ushort ShadowMeshIndex, ushort ShadowMeshCount,
    ushort TerrainShadowMeshIndex, ushort TerrainShadowMeshCount, ushort VerticalFogMeshIndex,
    ushort VerticalFogMeshCount, uint EdgeGeometrySize, uint EdgeGeometryOffset, uint PolygonCount,
    byte NeckMorphOffset, byte NeckMorphCount, ushort Unknown, uint VertexBufferSize,
    uint IndexBufferSize, uint VertexDataOffset, uint IndexDataOffset);

public sealed record MdlExtraLod(ImmutableArray<ushort> Values);

public sealed record MdlMesh(ushort VertexCount, ushort Reserved, uint IndexCount,
    ushort MaterialIndex, ushort SubmeshIndex, ushort SubmeshCount, ushort BoneTableIndex,
    uint StartIndex, ImmutableArray<uint> VertexBufferOffsets,
    ImmutableArray<byte> VertexBufferStrides, byte VertexStreamCount);

public sealed record MdlTerrainShadowMesh(uint IndexCount, uint StartIndex, uint VertexBufferOffset,
    ushort VertexCount, ushort SubmeshIndex, ushort SubmeshCount, byte VertexBufferStride, byte Reserved);

public sealed record MdlSubmesh(uint IndexOffset, uint IndexCount, uint AttributeMask,
    ushort BoneMapStart, ushort BoneMapCount);

public sealed record MdlTerrainShadowSubmesh(uint IndexOffset, uint IndexCount, ushort Unknown1, ushort Unknown2);

public sealed record MdlBoneTable(ImmutableArray<ushort> BoneIndices);
public sealed record MdlShape(string Name, uint NameOffset, ImmutableArray<ushort> MeshStarts,
    ImmutableArray<ushort> MeshCounts);
public sealed record MdlShapeMesh(uint MeshIndexOffset, uint ShapeValueCount, uint ShapeValueOffset);
public sealed record MdlShapeValue(ushort BaseIndexOffset, ushort ReplacementVertexIndex);
public sealed record MdlNeckMorph(Vector3 Position, uint Unknown, Vector3 Normal,
    ImmutableArray<byte> BoneIndices);
public sealed record MdlFaceData(Vector3 Position, uint Sign);
public sealed record MdlBoundingBox(Vector4 Minimum, Vector4 Maximum);

/// <summary>
/// Checked reader and lossless writer for an uncompressed Dawntrail MDL payload.
/// Unknown and reserved bytes remain in their original locations. Mutations used by
/// race conversion are fixed-size, so writing never discards newer format data.
/// </summary>
public sealed class MdlFile
{
    public const uint Version5 = 0x01000005;
    public const uint Version6 = 0x01000006;
    private const int HeaderSize = 0x44;
    private const int VertexDeclarationSize = 17 * 8;

    private byte[] _bytes;

    public MdlFileHeader Header { get; }
    public MdlModelHeader ModelHeader { get; private set; }
    public ImmutableArray<MdlVertexDeclaration> VertexDeclarations { get; private set; }
    public ImmutableArray<string> Strings { get; private set; }
    public ImmutableArray<string> Attributes { get; private set; }
    public ImmutableArray<string> Materials { get; private set; }
    public ImmutableArray<string> Bones { get; private set; }
    public ImmutableArray<MdlElementId> ElementIds { get; }
    public ImmutableArray<MdlLod> Lods { get; private set; }
    public ImmutableArray<MdlExtraLod> ExtraLods { get; }
    public ImmutableArray<MdlMesh> Meshes { get; private set; }
    public ImmutableArray<MdlTerrainShadowMesh> TerrainShadowMeshes { get; }
    public ImmutableArray<MdlSubmesh> Submeshes { get; private set; }
    public ImmutableArray<MdlTerrainShadowSubmesh> TerrainShadowSubmeshes { get; }
    public ImmutableArray<MdlBoneTable> BoneTables { get; }
    public ImmutableArray<MdlShape> Shapes { get; private set; }
    public ImmutableArray<MdlShapeMesh> ShapeMeshes { get; private set; }
    public ImmutableArray<MdlShapeValue> ShapeValues { get; }
    public ImmutableArray<ushort> SubmeshBoneMap { get; }
    public ImmutableArray<MdlNeckMorph> NeckMorphs { get; }
    public ImmutableArray<MdlFaceData> FaceData { get; private set; }
    public MdlBoundingBox BoundingBox { get; private set; }
    public MdlBoundingBox ModelBoundingBox { get; private set; }
    public MdlBoundingBox WaterBoundingBox { get; }
    public MdlBoundingBox VerticalFogBoundingBox { get; }
    public ImmutableArray<MdlBoundingBox> BoneBoundingBoxes { get; private set; }
    public int DataOffset { get; }
    public int BoundsOffset { get; }
    private ushort StringTableUnknown { get; init; }
    private ImmutableArray<byte> MetadataTail { get; init; }
    private bool RequiresRebuild { get; set; }

    private MdlFile(byte[] bytes, MdlFileHeader header, MdlModelHeader modelHeader,
        ImmutableArray<MdlVertexDeclaration> vertexDeclarations, ImmutableArray<string> strings,
        ImmutableArray<string> attributes, ImmutableArray<string> materials, ImmutableArray<string> bones,
        ImmutableArray<MdlElementId> elementIds, ImmutableArray<MdlLod> lods,
        ImmutableArray<MdlExtraLod> extraLods, ImmutableArray<MdlMesh> meshes,
        ImmutableArray<MdlTerrainShadowMesh> terrainShadowMeshes, ImmutableArray<MdlSubmesh> submeshes,
        ImmutableArray<MdlTerrainShadowSubmesh> terrainShadowSubmeshes,
        ImmutableArray<MdlBoneTable> boneTables, ImmutableArray<MdlShape> shapes,
        ImmutableArray<MdlShapeMesh> shapeMeshes, ImmutableArray<MdlShapeValue> shapeValues,
        ImmutableArray<ushort> submeshBoneMap, ImmutableArray<MdlNeckMorph> neckMorphs,
        ImmutableArray<MdlFaceData> faceData, MdlBoundingBox boundingBox,
        MdlBoundingBox modelBoundingBox, MdlBoundingBox waterBoundingBox,
        MdlBoundingBox verticalFogBoundingBox, ImmutableArray<MdlBoundingBox> boneBoundingBoxes,
        int dataOffset, int boundsOffset)
    {
        _bytes = bytes;
        Header = header;
        ModelHeader = modelHeader;
        VertexDeclarations = vertexDeclarations;
        Strings = strings;
        Attributes = attributes;
        Materials = materials;
        Bones = bones;
        ElementIds = elementIds;
        Lods = lods;
        ExtraLods = extraLods;
        Meshes = meshes;
        TerrainShadowMeshes = terrainShadowMeshes;
        Submeshes = submeshes;
        TerrainShadowSubmeshes = terrainShadowSubmeshes;
        BoneTables = boneTables;
        Shapes = shapes;
        ShapeMeshes = shapeMeshes;
        ShapeValues = shapeValues;
        SubmeshBoneMap = submeshBoneMap;
        NeckMorphs = neckMorphs;
        FaceData = faceData;
        BoundingBox = boundingBox;
        ModelBoundingBox = modelBoundingBox;
        WaterBoundingBox = waterBoundingBox;
        VerticalFogBoundingBox = verticalFogBoundingBox;
        BoneBoundingBoxes = boneBoundingBoxes;
        DataOffset = dataOffset;
        BoundsOffset = boundsOffset;
    }

    public static MdlFile Read(ReadOnlyMemory<byte> input)
    {
        if (input.Length < HeaderSize) throw new InvalidDataException("MDL header is truncated.");
        var bytes = input.ToArray();
        var r = new CheckedReader(bytes);
        var version = r.U32();
        if (version == Version5)
            throw new UnsupportedMdlVersionException(version, "MDL version 5 is not supported for race conversion.");
        if (version != Version6)
            throw new UnsupportedMdlVersionException(version, $"Unsupported MDL version 0x{version:X8}.");

        var stackSize = r.U32();
        var runtimeSize = r.U32();
        var declarationCount = r.U16();
        var materialCount = r.U16();
        var vertexOffsets = r.U32s(3);
        var indexOffsets = r.U32s(3);
        var vertexSizes = r.U32s(3);
        var indexSizes = r.U32s(3);
        var lodCount = r.U8();
        var indexStreaming = r.U8() != 0;
        var edgeGeometry = r.U8() != 0;
        var headerReserved = r.U8();
        if (declarationCount > 4096 || lodCount > 3)
            throw new InvalidDataException("MDL header contains impossible counts.");
        if (stackSize != declarationCount * VertexDeclarationSize)
            throw new InvalidDataException("MDL vertex declaration stack size is inconsistent.");
        var dataOffset64 = HeaderSize + (long)stackSize + runtimeSize;
        if (dataOffset64 < HeaderSize || dataOffset64 > bytes.Length)
            throw new InvalidDataException("MDL data offset is outside the file.");
        var dataOffset = (int)dataOffset64;
        var header = new MdlFileHeader(version, stackSize, runtimeSize, declarationCount, materialCount,
            vertexOffsets, indexOffsets, vertexSizes, indexSizes, lodCount, indexStreaming,
            edgeGeometry, headerReserved);

        var declarations = ImmutableArray.CreateBuilder<MdlVertexDeclaration>(declarationCount);
        for (var d = 0; d < declarationCount; d++)
        {
            var start = r.Position;
            var elements = ImmutableArray.CreateBuilder<MdlVertexElement>();
            var terminated = false;
            for (var i = 0; i < 17; i++)
            {
                var stream = r.U8();
                var offset = r.U8();
                var type = r.U8();
                var usage = r.U8();
                var usageIndex = r.U8();
                var reserved = r.Bytes(3).ToImmutableArray();
                if (stream == byte.MaxValue) { terminated = true; break; }
                if (stream > 2 || type > 17 || usage > 7)
                    throw new InvalidDataException($"MDL vertex declaration {d} contains an unsupported element.");
                elements.Add(new MdlVertexElement(stream, offset, (MdlVertexType)type,
                    (MdlVertexUsage)usage, usageIndex, reserved));
            }
            if (!terminated) throw new InvalidDataException($"MDL vertex declaration {d} is unterminated.");
            r.Position = start + VertexDeclarationSize;
            declarations.Add(new MdlVertexDeclaration(elements.ToImmutable(),
                bytes.AsSpan(start, VertexDeclarationSize).ToArray().ToImmutableArray()));
        }

        var stringCount = r.U16();
        var stringUnknown = r.U16();
        var stringSize = r.U32();
        if (stringCount > 16384 || stringSize > int.MaxValue)
            throw new InvalidDataException("MDL string table is too large.");
        var stringData = r.Bytes((int)stringSize);
        var stringList = ImmutableArray.CreateBuilder<string>(stringCount);
        var stringOffsets = new Dictionary<uint, string>();
        var stringPosition = 0;
        for (var i = 0; i < stringCount; i++)
        {
            if (stringPosition >= stringData.Length)
                throw new InvalidDataException("MDL string table count exceeds its data.");
            var end = stringPosition;
            while (end < stringData.Length && stringData[end] != 0) end++;
            if (end == stringData.Length) throw new InvalidDataException("MDL string table is unterminated.");
            var value = Encoding.UTF8.GetString(stringData[stringPosition..end]);
            stringList.Add(value);
            stringOffsets.Add((uint)stringPosition, value);
            stringPosition = end + 1;
        }

        var radiusOffset = r.Position;
        var modelHeader = new MdlModelHeader(r.F32(), r.U16(), r.U16(), r.U16(), r.U16(), r.U16(),
            r.U16(), r.U16(), r.U16(), r.U16(), r.U8(), r.U8(), r.U16(), r.U8(), r.U8(),
            r.F32(), r.F32(), r.U16(), r.U16(), r.U8(), r.U8(), r.U8(), r.U8(),
            r.U16(), r.U16(), r.U32(), r.Bytes(4).ToImmutableArray());
        ValidateCounts(modelHeader, declarationCount, materialCount, lodCount);

        var elementsIds = ImmutableArray.CreateBuilder<MdlElementId>(modelHeader.ElementIdCount);
        for (var i = 0; i < modelHeader.ElementIdCount; i++)
            elementsIds.Add(new MdlElementId(r.U32(), r.U32(), r.Vector3(), r.Vector3()));

        var lods = ImmutableArray.CreateBuilder<MdlLod>(3);
        for (var i = 0; i < 3; i++)
            lods.Add(new MdlLod(r.U16(), r.U16(), r.F32(), r.F32(), r.U16(), r.U16(), r.U16(), r.U16(),
                r.U16(), r.U16(), r.U16(), r.U16(), r.U32(), r.U32(), r.U32(), r.U8(), r.U8(), r.U16(),
                r.U32(), r.U32(), r.U32(), r.U32()));

        var extraLods = ImmutableArray.CreateBuilder<MdlExtraLod>();
        if ((modelHeader.Flags2 & 0x10) != 0)
            for (var i = 0; i < 3; i++) extraLods.Add(new MdlExtraLod(r.U16s(20)));

        var meshes = ImmutableArray.CreateBuilder<MdlMesh>(modelHeader.MeshCount);
        for (var i = 0; i < modelHeader.MeshCount; i++)
            meshes.Add(new MdlMesh(r.U16(), r.U16(), r.U32(), r.U16(), r.U16(), r.U16(), r.U16(),
                r.U32(), r.U32s(3), r.Bytes(3).ToImmutableArray(), r.U8()));

        var attributes = ReadStringReferences(r, modelHeader.AttributeCount, stringOffsets, "attribute");
        var terrainMeshes = ImmutableArray.CreateBuilder<MdlTerrainShadowMesh>(modelHeader.TerrainShadowMeshCount);
        for (var i = 0; i < modelHeader.TerrainShadowMeshCount; i++)
            terrainMeshes.Add(new MdlTerrainShadowMesh(r.U32(), r.U32(), r.U32(), r.U16(), r.U16(),
                r.U16(), r.U8(), r.U8()));
        var submeshes = ImmutableArray.CreateBuilder<MdlSubmesh>(modelHeader.SubmeshCount);
        for (var i = 0; i < modelHeader.SubmeshCount; i++)
            submeshes.Add(new MdlSubmesh(r.U32(), r.U32(), r.U32(), r.U16(), r.U16()));
        var terrainSubmeshes = ImmutableArray.CreateBuilder<MdlTerrainShadowSubmesh>(modelHeader.TerrainShadowSubmeshCount);
        for (var i = 0; i < modelHeader.TerrainShadowSubmeshCount; i++)
            terrainSubmeshes.Add(new MdlTerrainShadowSubmesh(r.U32(), r.U32(), r.U16(), r.U16()));
        var materials = ReadStringReferences(r, modelHeader.MaterialCount, stringOffsets, "material");
        var bones = ReadStringReferences(r, modelHeader.BoneCount, stringOffsets, "bone");

        var boneTableStart = r.Position;
        var boneTables = ImmutableArray.CreateBuilder<MdlBoneTable>(modelHeader.BoneTableCount);
        for (var i = 0; i < modelHeader.BoneTableCount; i++)
        {
            var entryStart = r.Position;
            var offsetWords = r.U16();
            var count = r.U16();
            var returnPosition = r.Position;
            var arrayPosition = checked(entryStart + offsetWords * 4);
            r.Position = arrayPosition;
            boneTables.Add(new MdlBoneTable(r.U16s(count)));
            r.Position = returnPosition;
        }
        r.Position = checked(boneTableStart + modelHeader.BoneTableCount * 4 + modelHeader.BoneTableArrayCount * 2);

        var shapes = ImmutableArray.CreateBuilder<MdlShape>(modelHeader.ShapeCount);
        for (var i = 0; i < modelHeader.ShapeCount; i++)
        {
            var nameOffset = r.U32();
            if (!stringOffsets.TryGetValue(nameOffset, out var name))
                throw new InvalidDataException("MDL shape references an unknown string.");
            shapes.Add(new MdlShape(name, nameOffset, r.U16s(3), r.U16s(3)));
        }
        var shapeMeshes = ImmutableArray.CreateBuilder<MdlShapeMesh>(modelHeader.ShapeMeshCount);
        for (var i = 0; i < modelHeader.ShapeMeshCount; i++)
            shapeMeshes.Add(new MdlShapeMesh(r.U32(), r.U32(), r.U32()));
        var shapeValues = ImmutableArray.CreateBuilder<MdlShapeValue>(modelHeader.ShapeValueCount);
        for (var i = 0; i < modelHeader.ShapeValueCount; i++)
            shapeValues.Add(new MdlShapeValue(r.U16(), r.U16()));

        var submeshBoneMapSize = r.U32();
        if ((submeshBoneMapSize & 1) != 0 || submeshBoneMapSize > int.MaxValue)
            throw new InvalidDataException("MDL submesh bone-map size is invalid.");
        var submeshBoneMap = r.U16s((int)submeshBoneMapSize / 2);
        // Penumbra's MDL writer stores the neck-morph count but never the neck-morph data,
        // so models exported through it announce morphs that are not there. Read them when
        // the layout fits and fall back to "count without data" when it overruns.
        // Prefer the layout that ends exactly where the vertex data starts.
        var tailStart = r.Position;
        var withMorphs = ReadTail(r, modelHeader, withNeckMorphs: true, dataOffset);
        var withMorphsEnd = r.Position;
        var neckMorphDataMissing = false;
        var tail = withMorphs;
        if (modelHeader.NeckMorphCount > 0 && (withMorphs == null || withMorphsEnd != dataOffset))
        {
            r.Position = tailStart;
            var withoutMorphs = ReadTail(r, modelHeader, withNeckMorphs: false, dataOffset);
            if (withoutMorphs != null && (withMorphs == null || r.Position == dataOffset))
            {
                tail = withoutMorphs;
                neckMorphDataMissing = true;
            }
            else r.Position = withMorphsEnd;
        }
        if (tail == null)
            throw new InvalidDataException("MDL metadata overlaps its vertex buffers.");
        var (neckMorphs, faceDataStart, faceData, boundsOffset, boundingBox, modelBoundingBox, waterBoundingBox,
            verticalFogBoundingBox, boneBounds) = tail.Value;
        var metadataTail = r.Bytes(dataOffset - r.Position).ToArray().ToImmutableArray();

        ValidateBufferRanges(bytes.Length, dataOffset, header, lods, meshes, declarations);
        return new MdlFile(bytes, header, modelHeader, declarations.ToImmutable(),
            stringList.ToImmutable(), attributes, materials, bones, elementsIds.ToImmutable(),
            lods.ToImmutable(), extraLods.ToImmutable(), meshes.ToImmutable(),
            terrainMeshes.ToImmutable(), submeshes.ToImmutable(), terrainSubmeshes.ToImmutable(),
            boneTables.ToImmutable(), shapes.ToImmutable(), shapeMeshes.ToImmutable(),
            shapeValues.ToImmutable(), submeshBoneMap, neckMorphs.ToImmutable(),
            faceData.ToImmutable(), boundingBox, modelBoundingBox, waterBoundingBox,
            verticalFogBoundingBox, boneBounds.ToImmutable(), dataOffset, boundsOffset)
        {
            RadiusOffset = radiusOffset,
            FaceDataOffset = faceDataStart,
            StringTableUnknown = stringUnknown,
            MetadataTail = metadataTail,
            NeckMorphDataMissing = neckMorphDataMissing,
        };
    }

    private static (ImmutableArray<MdlNeckMorph>.Builder NeckMorphs, int FaceDataStart,
        ImmutableArray<MdlFaceData>.Builder FaceData, int BoundsOffset, MdlBoundingBox Bounding, MdlBoundingBox Model,
        MdlBoundingBox Water, MdlBoundingBox VerticalFog, ImmutableArray<MdlBoundingBox>.Builder BoneBounds)?
        ReadTail(CheckedReader r, MdlModelHeader modelHeader, bool withNeckMorphs, int dataOffset)
    {
        try
        {
            var neckMorphs = ImmutableArray.CreateBuilder<MdlNeckMorph>(modelHeader.NeckMorphCount);
            if (withNeckMorphs)
                for (var i = 0; i < modelHeader.NeckMorphCount; i++)
                    neckMorphs.Add(new MdlNeckMorph(r.Vector3(), r.U32(), r.Vector3(), r.Bytes(4).ToImmutableArray()));

            var faceDataStart = r.Position;
            var faceData = ImmutableArray.CreateBuilder<MdlFaceData>(checked((int)modelHeader.FaceDataCount));
            for (var i = 0; i < modelHeader.FaceDataCount; i++) faceData.Add(new MdlFaceData(r.Vector3(), r.U32()));
            var padding = r.U8();
            r.Skip(padding);
            var boundsOffset = r.Position;
            var boundingBox = ReadBounds(r);
            var modelBoundingBox = ReadBounds(r);
            var waterBoundingBox = ReadBounds(r);
            var verticalFogBoundingBox = ReadBounds(r);
            var boneBounds = ImmutableArray.CreateBuilder<MdlBoundingBox>(modelHeader.BoneCount);
            for (var i = 0; i < modelHeader.BoneCount; i++) boneBounds.Add(ReadBounds(r));
            if (r.Position > dataOffset) return null;
            return (neckMorphs, faceDataStart, faceData, boundsOffset, boundingBox, modelBoundingBox, waterBoundingBox,
                verticalFogBoundingBox, boneBounds);
        }
        catch (InvalidDataException) when (withNeckMorphs)
        {
            return null;
        }
    }

    /// <summary>True when the header announces neck morphs whose data the file does not contain.</summary>
    private bool NeckMorphDataMissing { get; init; }

    private int RadiusOffset { get; init; }
    private int FaceDataOffset { get; init; }

    public byte[] Write() => RequiresRebuild ? WriteRebuilt() : _bytes.ToArray();

    /// <summary>Applies same-length path edits while keeping all section offsets stable.</summary>
    public void ReplacePaths(IReadOnlyDictionary<string, string> replacements)
    {
        foreach (var (source, target) in replacements)
        {
            var found = false;
            for (var i = 0; i < Strings.Length; i++)
                if (string.Equals(Strings[i], source, StringComparison.Ordinal))
                {
                    SetString(i, target);
                    found = true;
                }
            if (!found) throw new InvalidDataException($"Planned MDL path was not found in the string table: {source}");
        }
    }

    /// <summary>Replaces one string-table entry; length changes trigger a canonical rebuild.</summary>
    public void SetString(int index, string value)
    {
        if ((uint)index >= (uint)Strings.Length) throw new ArgumentOutOfRangeException(nameof(index));
        ArgumentNullException.ThrowIfNull(value);
        if (value.IndexOf('\0') >= 0) throw new ArgumentException("MDL strings cannot contain NUL bytes.", nameof(value));
        var oldValue = Strings[index];
        if (oldValue == value) return;
        var strings = Strings.ToArray();
        strings[index] = value;
        Strings = strings.ToImmutableArray();
        Attributes = Attributes.Select(v => v == oldValue ? value : v).ToImmutableArray();
        Materials = Materials.Select(v => v == oldValue ? value : v).ToImmutableArray();
        Bones = Bones.Select(v => v == oldValue ? value : v).ToImmutableArray();
        Shapes = Shapes.Select(s => s.Name == oldValue ? s with { Name = value } : s).ToImmutableArray();
        RequiresRebuild = true;
    }

    /// <summary>
    /// Removes meshes (by absolute mesh-table index) from the model. Only metadata changes:
    /// the vertex and index data of removed meshes stays in the buffers, unreferenced, so no
    /// buffer offset moves. Submeshes, vertex declarations, LOD mesh ranges and shape meshes
    /// of the removed meshes are dropped and every remaining index is renumbered.
    /// </summary>
    public void RemoveMeshes(IReadOnlyCollection<int> meshIndices)
    {
        var remove = meshIndices.ToHashSet();
        if (remove.Count == 0) return;
        if (remove.Any(i => (uint)i >= (uint)Meshes.Length))
            throw new ArgumentOutOfRangeException(nameof(meshIndices), "A mesh index is outside the mesh table.");
        if (Header.EdgeGeometryEnabled || Lods.Any(l => l.EdgeGeometrySize != 0))
            throw new InvalidDataException("Meshes cannot be removed from MDL files with edge geometry.");
        if (ExtraLods.Any(e => e.Values.Any(v => v != 0)))
            throw new InvalidDataException("Meshes cannot be removed from MDL files with extra LOD mesh ranges.");
        var lod0 = Lods[0];
        if (Enumerable.Range(lod0.MeshIndex, lod0.MeshCount).All(remove.Contains))
            throw new InvalidDataException("At least one mesh of the model must remain.");

        int KeptBefore(int index) => index - remove.Count(r => r < index);
        (ushort Start, ushort Count) Remap(ushort start, ushort count)
            => (checked((ushort)KeptBefore(start)),
                checked((ushort)Enumerable.Range(start, count).Count(i => !remove.Contains(i))));

        // Shape meshes are tied to a mesh by its first index within the same LOD.
        var removedStarts = new HashSet<uint>[Lods.Length];
        for (var l = 0; l < Lods.Length; l++)
        {
            var lod = Lods[l];
            removedStarts[l] = Enumerable.Range(lod.MeshIndex, lod.MeshCount)
                .Where(i => i < Meshes.Length && remove.Contains(i))
                .Select(i => Meshes[i].StartIndex)
                .ToHashSet();
        }
        var droppedShapeMeshes = new HashSet<int>();
        foreach (var shape in Shapes)
            for (var l = 0; l < Math.Min(3, Lods.Length); l++)
                for (var s = shape.MeshStarts[l]; s < shape.MeshStarts[l] + shape.MeshCounts[l]; s++)
                {
                    if (s >= ShapeMeshes.Length) throw new InvalidDataException("MDL shape references a missing shape mesh.");
                    if (removedStarts[l].Contains(ShapeMeshes[s].MeshIndexOffset)) droppedShapeMeshes.Add(s);
                }

        var meshes = ImmutableArray.CreateBuilder<MdlMesh>();
        var declarations = ImmutableArray.CreateBuilder<MdlVertexDeclaration>();
        var submeshes = ImmutableArray.CreateBuilder<MdlSubmesh>();
        for (var i = 0; i < Meshes.Length; i++)
        {
            if (remove.Contains(i)) continue;
            var mesh = Meshes[i];
            if (mesh.SubmeshIndex + mesh.SubmeshCount > Submeshes.Length)
                throw new InvalidDataException($"MDL mesh {i} references submeshes outside the submesh table.");
            meshes.Add(mesh with { SubmeshIndex = checked((ushort)submeshes.Count) });
            declarations.Add(VertexDeclarations[i]);
            for (var s = 0; s < mesh.SubmeshCount; s++) submeshes.Add(Submeshes[mesh.SubmeshIndex + s]);
        }

        Lods = Lods.Select(lod =>
        {
            var main = Remap(lod.MeshIndex, lod.MeshCount);
            var water = Remap(lod.WaterMeshIndex, lod.WaterMeshCount);
            var shadow = Remap(lod.ShadowMeshIndex, lod.ShadowMeshCount);
            var fog = Remap(lod.VerticalFogMeshIndex, lod.VerticalFogMeshCount);
            return lod with
            {
                MeshIndex = main.Start, MeshCount = main.Count,
                WaterMeshIndex = water.Start, WaterMeshCount = water.Count,
                ShadowMeshIndex = shadow.Start, ShadowMeshCount = shadow.Count,
                VerticalFogMeshIndex = fog.Start, VerticalFogMeshCount = fog.Count,
            };
        }).ToImmutableArray();

        int ShapeKeptBefore(int index) => index - droppedShapeMeshes.Count(d => d < index);
        Shapes = Shapes.Select(shape =>
        {
            var starts = new ushort[3];
            var counts = new ushort[3];
            for (var l = 0; l < 3; l++)
            {
                starts[l] = checked((ushort)ShapeKeptBefore(shape.MeshStarts[l]));
                counts[l] = checked((ushort)Enumerable.Range(shape.MeshStarts[l], shape.MeshCounts[l])
                    .Count(s => !droppedShapeMeshes.Contains(s)));
            }
            return shape with { MeshStarts = starts.ToImmutableArray(), MeshCounts = counts.ToImmutableArray() };
        }).ToImmutableArray();
        // Values of dropped shape meshes stay in the value table, unreferenced.
        ShapeMeshes = ShapeMeshes.Where((_, i) => !droppedShapeMeshes.Contains(i)).ToImmutableArray();

        Meshes = meshes.ToImmutable();
        VertexDeclarations = declarations.ToImmutable();
        Submeshes = submeshes.ToImmutable();
        RequiresRebuild = true;
    }

    /// <summary>
    /// Drops materials no mesh uses and renumbers the rest. The game loads every material a
    /// model lists before it draws the model, used or not, so one that cannot be found — a
    /// skin material left behind by a removed skin part, say — keeps the whole model from
    /// showing. Returns the names that were dropped.
    /// </summary>
    public IReadOnlyList<string> RemoveUnusedMaterials()
    {
        var used = Meshes.Select(m => (int)m.MaterialIndex).Where(i => i < Materials.Length).ToHashSet();
        // Crest and background materials are named by the header rather than by a mesh.
        foreach (var index in new int[] { ModelHeader.BackgroundMaterialIndex, ModelHeader.CrestMaterialIndex })
            if (index != 0 && index < Materials.Length) used.Add(index);
        if (used.Count == Materials.Length) return [];

        var map = new int[Materials.Length];
        var kept = ImmutableArray.CreateBuilder<string>();
        var removed = new List<string>();
        for (var i = 0; i < Materials.Length; i++)
        {
            if (used.Contains(i))
            {
                map[i] = kept.Count;
                kept.Add(Materials[i]);
            }
            else removed.Add(Materials[i]);
        }

        ushort Remap(ushort index) => index < map.Length && used.Contains(index) ? checked((ushort)map[index]) : (ushort)0;
        Meshes = Meshes.Select(m => m with { MaterialIndex = Remap(m.MaterialIndex) }).ToImmutableArray();
        ModelHeader = ModelHeader with
        {
            BackgroundMaterialIndex = (byte)Remap(ModelHeader.BackgroundMaterialIndex),
            CrestMaterialIndex = (byte)Remap(ModelHeader.CrestMaterialIndex),
        };
        Materials = kept.ToImmutable();
        RequiresRebuild = true;
        return removed;
    }

    /// <summary>
    /// Hides submeshes (by absolute submesh-table index) by turning their triangles into
    /// zero-area ones: every index of the submesh's range is set to its first index. Nothing
    /// moves and no table changes, so this is safe whether the game draws a mesh as a whole or
    /// submesh by submesh, and a shape that replaces one of those indices still yields
    /// zero-area triangles.
    /// </summary>
    public void RemoveSubmeshes(IReadOnlyCollection<int> submeshIndices)
    {
        foreach (var index in submeshIndices)
        {
            if ((uint)index >= (uint)Submeshes.Length)
                throw new ArgumentOutOfRangeException(nameof(submeshIndices), "A submesh index is outside the submesh table.");
            var mesh = -1;
            for (var m = 0; m < Meshes.Length && mesh < 0; m++)
                if (index >= Meshes[m].SubmeshIndex && index < Meshes[m].SubmeshIndex + Meshes[m].SubmeshCount)
                    mesh = m;
            if (mesh < 0) throw new InvalidDataException($"MDL submesh {index} belongs to no mesh.");
            var lod = -1;
            for (var l = 0; l < Math.Min(Header.LodCount, Lods.Length) && lod < 0; l++)
                if (mesh >= Lods[l].MeshIndex && mesh < Lods[l].MeshIndex + Lods[l].MeshCount)
                    lod = l;
            if (lod < 0) throw new InvalidDataException($"MDL mesh {mesh} belongs to no LOD.");

            var submesh = Submeshes[index];
            if (submesh.IndexCount == 0) continue;
            // Submesh index offsets count from the start of their LOD's index buffer.
            var end = ((long)submesh.IndexOffset + submesh.IndexCount) * 2;
            if (end > Lods[lod].IndexBufferSize)
                throw new InvalidDataException($"MDL submesh {index} exceeds its LOD's index buffer.");
            var span = _bytes.AsSpan(checked((int)(Lods[lod].IndexDataOffset + submesh.IndexOffset * 2)),
                checked((int)submesh.IndexCount * 2));
            var first = BinaryPrimitives.ReadUInt16LittleEndian(span);
            for (var i = 2; i < span.Length; i += 2) BinaryPrimitives.WriteUInt16LittleEndian(span[i..], first);
        }
    }

    internal Span<byte> MutableBytes => _bytes;

    internal int VertexOffset(int lodIndex, int meshIndex, int stream, int vertexIndex)
    {
        var lod = Lods[lodIndex];
        var mesh = Meshes[meshIndex];
        return checked((int)lod.VertexDataOffset + (int)mesh.VertexBufferOffsets[stream]
            + vertexIndex * mesh.VertexBufferStrides[stream]);
    }

    internal void SetFaceData(IReadOnlyList<Vector3> positions)
    {
        if (positions.Count != FaceData.Length)
            throw new InvalidDataException("MDL face-data count does not match transformed vertices.");
        var builder = ImmutableArray.CreateBuilder<MdlFaceData>(positions.Count);
        for (var i = 0; i < positions.Count; i++)
        {
            WriteVector3(FaceDataOffset + i * 16, positions[i]);
            builder.Add(FaceData[i] with { Position = positions[i] });
        }
        FaceData = builder.MoveToImmutable();
    }

    internal void SetBounds(MdlBoundingBox modelBounds, IReadOnlyList<MdlBoundingBox> boneBounds)
    {
        if (boneBounds.Count != Bones.Length) throw new InvalidDataException("MDL bone-bound count is invalid.");
        BoundingBox = modelBounds;
        ModelBoundingBox = modelBounds;
        BoneBoundingBoxes = boneBounds.ToImmutableArray();
        WriteBounds(BoundsOffset, modelBounds);
        WriteBounds(BoundsOffset + 32, modelBounds);
        for (var i = 0; i < boneBounds.Count; i++) WriteBounds(BoundsOffset + 128 + i * 32, boneBounds[i]);
        var radius = new Vector3(
            MathF.Max(MathF.Abs(modelBounds.Minimum.X), MathF.Abs(modelBounds.Maximum.X)),
            MathF.Max(MathF.Abs(modelBounds.Minimum.Y), MathF.Abs(modelBounds.Maximum.Y)),
            MathF.Max(MathF.Abs(modelBounds.Minimum.Z), MathF.Abs(modelBounds.Maximum.Z))).Length();
        WriteSingle(RadiusOffset, radius);
        ModelHeader = ModelHeader with { Radius = radius };
    }

    private void WriteBounds(int offset, MdlBoundingBox bounds)
    {
        WriteVector4(offset, bounds.Minimum);
        WriteVector4(offset + 16, bounds.Maximum);
    }

    private void WriteVector3(int offset, Vector3 value)
    {
        WriteSingle(offset, value.X); WriteSingle(offset + 4, value.Y); WriteSingle(offset + 8, value.Z);
    }

    private void WriteVector4(int offset, Vector4 value)
    {
        WriteSingle(offset, value.X); WriteSingle(offset + 4, value.Y);
        WriteSingle(offset + 8, value.Z); WriteSingle(offset + 12, value.W);
    }

    private void WriteSingle(int offset, float value)
        => BinaryPrimitives.WriteInt32LittleEndian(_bytes.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(value));

    /// <summary>Serializes every known v6 metadata section and relocates the original data buffers.</summary>
    public byte[] WriteRebuilt()
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.UTF8, true);
        w.Write(new byte[HeaderSize]);
        foreach (var declaration in VertexDeclarations)
        {
            if (declaration.ReservedBytes.Length != VertexDeclarationSize)
                throw new InvalidDataException("MDL vertex declaration backing data is invalid.");
            w.Write(declaration.ReservedBytes.AsSpan());
        }

        var stringOffsets = new uint[Strings.Length];
        w.Write(checked((ushort)Strings.Length));
        w.Write(StringTableUnknown);
        var stringSizePosition = stream.Position;
        w.Write(0u);
        var stringDataStart = stream.Position;
        for (var i = 0; i < Strings.Length; i++)
        {
            stringOffsets[i] = checked((uint)(stream.Position - stringDataStart));
            w.Write(Encoding.UTF8.GetBytes(Strings[i]));
            w.Write((byte)0);
        }
        while (((stream.Position - stringDataStart) & 3) != 0) w.Write((byte)0);
        var stringEnd = stream.Position;
        stream.Position = stringSizePosition;
        w.Write(checked((uint)(stringEnd - stringDataStart)));
        stream.Position = stringEnd;

        var radiusPosition = stream.Position;
        WriteModelHeader(w, ModelHeader with
        {
            MeshCount = checked((ushort)Meshes.Length),
            AttributeCount = checked((ushort)Attributes.Length),
            SubmeshCount = checked((ushort)Submeshes.Length),
            MaterialCount = checked((ushort)Materials.Length),
            BoneCount = checked((ushort)Bones.Length),
            BoneTableCount = checked((ushort)BoneTables.Length),
            ShapeCount = checked((ushort)Shapes.Length),
            ShapeMeshCount = checked((ushort)ShapeMeshes.Length),
            ShapeValueCount = checked((ushort)ShapeValues.Length),
            ElementIdCount = checked((ushort)ElementIds.Length),
            TerrainShadowMeshCount = checked((byte)TerrainShadowMeshes.Length),
            TerrainShadowSubmeshCount = checked((ushort)TerrainShadowSubmeshes.Length),
            NeckMorphCount = NeckMorphDataMissing ? ModelHeader.NeckMorphCount : checked((byte)NeckMorphs.Length),
            FaceDataCount = checked((uint)FaceData.Length),
            BoneTableArrayCount = checked((ushort)BoneTables.Sum(t => (t.BoneIndices.Length + 1) & ~1)),
        });
        foreach (var element in ElementIds)
        {
            w.Write(element.Id); w.Write(element.ParentBone);
            Write(w, element.Translation); Write(w, element.Rotation);
        }
        var lodPosition = stream.Position;
        w.Write(new byte[3 * 60]);
        foreach (var extra in ExtraLods)
        {
            if (extra.Values.Length != 20) throw new InvalidDataException("MDL extra LOD must contain 20 values.");
            foreach (var value in extra.Values) w.Write(value);
        }
        foreach (var mesh in Meshes) Write(w, mesh);
        foreach (var attribute in Attributes) w.Write(StringOffset(attribute));
        foreach (var mesh in TerrainShadowMeshes)
        {
            w.Write(mesh.IndexCount); w.Write(mesh.StartIndex); w.Write(mesh.VertexBufferOffset);
            w.Write(mesh.VertexCount); w.Write(mesh.SubmeshIndex); w.Write(mesh.SubmeshCount);
            w.Write(mesh.VertexBufferStride); w.Write(mesh.Reserved);
        }
        foreach (var submesh in Submeshes)
        {
            w.Write(submesh.IndexOffset); w.Write(submesh.IndexCount); w.Write(submesh.AttributeMask);
            w.Write(submesh.BoneMapStart); w.Write(submesh.BoneMapCount);
        }
        foreach (var submesh in TerrainShadowSubmeshes)
        {
            w.Write(submesh.IndexOffset); w.Write(submesh.IndexCount); w.Write(submesh.Unknown1); w.Write(submesh.Unknown2);
        }
        foreach (var material in Materials) w.Write(StringOffset(material));
        foreach (var bone in Bones) w.Write(StringOffset(bone));

        var boneTableStart = stream.Position;
        var arrayPosition = boneTableStart + BoneTables.Length * 4L;
        foreach (var table in BoneTables) arrayPosition += ((table.BoneIndices.Length + 1) & ~1) * 2L;
        stream.SetLength(arrayPosition);
        var nextArray = boneTableStart + BoneTables.Length * 4L;
        for (var i = 0; i < BoneTables.Length; i++)
        {
            var headerPosition = boneTableStart + i * 4L;
            var table = BoneTables[i];
            stream.Position = headerPosition;
            w.Write(checked((ushort)((nextArray - headerPosition) / 4)));
            w.Write(checked((ushort)table.BoneIndices.Length));
            stream.Position = nextArray;
            foreach (var bone in table.BoneIndices) w.Write(bone);
            if ((table.BoneIndices.Length & 1) != 0) w.Write((ushort)0);
            nextArray = stream.Position;
        }
        stream.Position = arrayPosition;

        foreach (var shape in Shapes)
        {
            w.Write(StringOffset(shape.Name));
            foreach (var value in shape.MeshStarts) w.Write(value);
            foreach (var value in shape.MeshCounts) w.Write(value);
        }
        foreach (var shape in ShapeMeshes)
        {
            w.Write(shape.MeshIndexOffset); w.Write(shape.ShapeValueCount); w.Write(shape.ShapeValueOffset);
        }
        foreach (var value in ShapeValues)
        {
            w.Write(value.BaseIndexOffset); w.Write(value.ReplacementVertexIndex);
        }
        w.Write(checked((uint)(SubmeshBoneMap.Length * 2)));
        foreach (var value in SubmeshBoneMap) w.Write(value);
        foreach (var morph in NeckMorphs)
        {
            Write(w, morph.Position); w.Write(morph.Unknown); Write(w, morph.Normal);
            if (morph.BoneIndices.Length != 4) throw new InvalidDataException("MDL neck morph bone array is invalid.");
            w.Write(morph.BoneIndices.AsSpan());
        }
        foreach (var face in FaceData) { Write(w, face.Position); w.Write(face.Sign); }

        var padding = (8 - (((int)stream.Position + 1) & 7)) & 7;
        w.Write((byte)padding);
        if (padding > 0)
        {
            ReadOnlySpan<byte> magic = [0xFE, 0xCA, 0x0D, 0xF0, 0xEF, 0xBE, 0xAD, 0xDE];
            w.Write(magic[..padding]);
        }
        Write(w, BoundingBox); Write(w, ModelBoundingBox); Write(w, WaterBoundingBox); Write(w, VerticalFogBoundingBox);
        foreach (var bounds in BoneBoundingBoxes) Write(w, bounds);
        w.Write(MetadataTail.AsSpan());
        var newDataOffset = checked((int)stream.Position);
        w.Write(_bytes.AsSpan(DataOffset));

        stream.Position = 0;
        var rebuiltHeader = Header with
        {
            StackSize = checked((uint)(VertexDeclarations.Length * VertexDeclarationSize)),
            RuntimeSize = checked((uint)(newDataOffset - HeaderSize - VertexDeclarations.Length * VertexDeclarationSize)),
            VertexDeclarationCount = checked((ushort)VertexDeclarations.Length),
            MaterialCount = checked((ushort)Materials.Length),
            VertexOffsets = Header.VertexOffsets.Select(Relocate).ToImmutableArray(),
            IndexOffsets = Header.IndexOffsets.Select(Relocate).ToImmutableArray(),
        };
        Write(w, rebuiltHeader);
        stream.Position = lodPosition;
        foreach (var lod in Lods)
            Write(w, lod with
            {
                VertexDataOffset = Relocate(lod.VertexDataOffset),
                IndexDataOffset = Relocate(lod.IndexDataOffset),
                EdgeGeometryOffset = lod.EdgeGeometryOffset == 0 ? 0 : Relocate(lod.EdgeGeometryOffset),
            });
        var result = stream.ToArray();
        _ = Read(result);
        return result;

        uint StringOffset(string value)
        {
            var index = Strings.IndexOf(value);
            if (index < 0) throw new InvalidDataException($"MDL reference string '{value}' is absent from the string table.");
            return stringOffsets[index];
        }

        uint Relocate(uint offset)
        {
            if (offset == 0) return 0;
            if (offset < DataOffset) throw new InvalidDataException("MDL data pointer precedes the data section.");
            return checked((uint)(newDataOffset + offset - DataOffset));
        }
    }

    private static void WriteModelHeader(BinaryWriter w, MdlModelHeader h)
    {
        w.Write(h.Radius); w.Write(h.MeshCount); w.Write(h.AttributeCount); w.Write(h.SubmeshCount);
        w.Write(h.MaterialCount); w.Write(h.BoneCount); w.Write(h.BoneTableCount); w.Write(h.ShapeCount);
        w.Write(h.ShapeMeshCount); w.Write(h.ShapeValueCount); w.Write(h.LodCount); w.Write(h.Flags1);
        w.Write(h.ElementIdCount); w.Write(h.TerrainShadowMeshCount); w.Write(h.Flags2);
        w.Write(h.ModelClipDistance); w.Write(h.ShadowClipDistance); w.Write(h.CullingGridCount);
        w.Write(h.TerrainShadowSubmeshCount); w.Write(h.Flags3); w.Write(h.BackgroundMaterialIndex);
        w.Write(h.CrestMaterialIndex); w.Write(h.NeckMorphCount); w.Write(h.BoneTableArrayCount);
        w.Write(h.Unknown); w.Write(h.FaceDataCount); w.Write(h.Reserved.AsSpan());
    }

    private static void Write(BinaryWriter w, MdlFileHeader h)
    {
        w.Write(h.Version); w.Write(h.StackSize); w.Write(h.RuntimeSize); w.Write(h.VertexDeclarationCount);
        w.Write(h.MaterialCount); foreach (var v in h.VertexOffsets) w.Write(v);
        foreach (var v in h.IndexOffsets) w.Write(v); foreach (var v in h.VertexBufferSizes) w.Write(v);
        foreach (var v in h.IndexBufferSizes) w.Write(v); w.Write(h.LodCount);
        w.Write((byte)(h.IndexBufferStreaming ? 1 : 0)); w.Write((byte)(h.EdgeGeometryEnabled ? 1 : 0));
        w.Write(h.Reserved);
    }

    private static void Write(BinaryWriter w, MdlLod l)
    {
        w.Write(l.MeshIndex); w.Write(l.MeshCount); w.Write(l.ModelRange); w.Write(l.TextureRange);
        w.Write(l.WaterMeshIndex); w.Write(l.WaterMeshCount); w.Write(l.ShadowMeshIndex); w.Write(l.ShadowMeshCount);
        w.Write(l.TerrainShadowMeshIndex); w.Write(l.TerrainShadowMeshCount); w.Write(l.VerticalFogMeshIndex);
        w.Write(l.VerticalFogMeshCount); w.Write(l.EdgeGeometrySize); w.Write(l.EdgeGeometryOffset);
        w.Write(l.PolygonCount); w.Write(l.NeckMorphOffset); w.Write(l.NeckMorphCount); w.Write(l.Unknown);
        w.Write(l.VertexBufferSize); w.Write(l.IndexBufferSize); w.Write(l.VertexDataOffset); w.Write(l.IndexDataOffset);
    }

    private static void Write(BinaryWriter w, MdlMesh m)
    {
        w.Write(m.VertexCount); w.Write(m.Reserved); w.Write(m.IndexCount); w.Write(m.MaterialIndex);
        w.Write(m.SubmeshIndex); w.Write(m.SubmeshCount); w.Write(m.BoneTableIndex); w.Write(m.StartIndex);
        foreach (var value in m.VertexBufferOffsets) w.Write(value);
        foreach (var value in m.VertexBufferStrides) w.Write(value);
        w.Write(m.VertexStreamCount);
    }

    private static void Write(BinaryWriter w, Vector3 value) { w.Write(value.X); w.Write(value.Y); w.Write(value.Z); }
    private static void Write(BinaryWriter w, Vector4 value) { w.Write(value.X); w.Write(value.Y); w.Write(value.Z); w.Write(value.W); }
    private static void Write(BinaryWriter w, MdlBoundingBox value) { Write(w, value.Minimum); Write(w, value.Maximum); }

    private static ImmutableArray<string> ReadStringReferences(CheckedReader r, int count,
        IReadOnlyDictionary<uint, string> strings, string kind)
    {
        var result = ImmutableArray.CreateBuilder<string>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = r.U32();
            if (!strings.TryGetValue(offset, out var value))
                throw new InvalidDataException($"MDL {kind} references an unknown string offset {offset}.");
            result.Add(value);
        }
        return result.MoveToImmutable();
    }

    private static MdlBoundingBox ReadBounds(CheckedReader r) => new(r.Vector4(), r.Vector4());

    private static void ValidateCounts(MdlModelHeader model, int declarations, int materials, int lods)
    {
        if (model.MeshCount != declarations || model.MaterialCount != materials || model.LodCount != lods)
            throw new InvalidDataException("MDL header counts are inconsistent.");
        if (model.MeshCount > 4096 || model.BoneCount > 4096 || model.SubmeshCount > 16384 ||
            model.ShapeValueCount > ushort.MaxValue || model.FaceDataCount > 10_000_000)
            throw new InvalidDataException("MDL metadata contains impossible counts.");
    }

    private static void ValidateBufferRanges(int fileLength, int dataOffset, MdlFileHeader header,
        ImmutableArray<MdlLod>.Builder lods, ImmutableArray<MdlMesh>.Builder meshes,
        ImmutableArray<MdlVertexDeclaration>.Builder declarations)
    {
        for (var l = 0; l < header.LodCount; l++)
        {
            var lod = lods[l];
            CheckRange(lod.VertexDataOffset, lod.VertexBufferSize, fileLength, dataOffset, "vertex buffer");
            CheckRange(lod.IndexDataOffset, lod.IndexBufferSize, fileLength, dataOffset, "index buffer");
            if (lod.MeshIndex + lod.MeshCount > meshes.Count)
                throw new InvalidDataException($"MDL LOD {l} references meshes outside the mesh table.");
            for (var m = lod.MeshIndex; m < lod.MeshIndex + lod.MeshCount; m++)
            {
                var mesh = meshes[m];
                // The stored stream count is not reliable in exported models (values above 3
                // occur); the vertex declaration defines which streams actually exist.
                var streams = declarations[m].Elements.Select(e => (int)e.Stream).Distinct().ToArray();
                foreach (var element in declarations[m].Elements)
                {
                    if (element.Offset + VertexTypeSize(element.Type, element.Usage) > mesh.VertexBufferStrides[element.Stream])
                        throw new InvalidDataException($"MDL mesh {m} vertex declaration exceeds its stride.");
                }
                foreach (var s in streams)
                {
                    var end = (long)mesh.VertexBufferOffsets[s] + (long)mesh.VertexCount * mesh.VertexBufferStrides[s];
                    if (end > lod.VertexBufferSize)
                        throw new InvalidDataException($"MDL mesh {m} stream {s} exceeds its LOD buffer.");
                }
            }
        }
    }

    internal static int VertexTypeSize(MdlVertexType type, MdlVertexUsage usage) => type switch
    {
        MdlVertexType.Single1 => 4, MdlVertexType.Single2 => 8, MdlVertexType.Single3 => 12,
        MdlVertexType.Single4 => 16, MdlVertexType.UByte4 or MdlVertexType.NByte4 => 4,
        MdlVertexType.Short2 or MdlVertexType.NShort2 or MdlVertexType.Half2 or MdlVertexType.UShort2 => 4,
        MdlVertexType.Short4 or MdlVertexType.NShort4 or MdlVertexType.Half4 => 8,
        MdlVertexType.UShort4 when usage is MdlVertexUsage.BlendWeights or MdlVertexUsage.BlendIndices => 8,
        MdlVertexType.UShort4 => 8,
        _ => throw new InvalidDataException($"Unsupported vertex type {type}."),
    };

    private static void CheckRange(uint offset, uint size, int length, int minimum, string label)
    {
        if (offset < minimum || (ulong)offset + size > (ulong)length)
            throw new InvalidDataException($"MDL {label} range is outside the file.");
    }

    private sealed class CheckedReader(byte[] data)
    {
        public int Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > data.Length) throw new InvalidDataException("MDL offset is outside the file.");
                _position = value;
            }
        }
        private int _position;
        public byte U8() { Need(1); return data[_position++]; }
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(_position, 2)); _position += 2; return v; }
        public uint U32() { Need(4); var v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(_position, 4)); _position += 4; return v; }
        public float F32() => BitConverter.Int32BitsToSingle(unchecked((int)U32()));
        public ImmutableArray<ushort> U16s(int count) { if (count < 0) throw new InvalidDataException("Negative MDL count."); var b = ImmutableArray.CreateBuilder<ushort>(count); for (var i = 0; i < count; i++) b.Add(U16()); return b.MoveToImmutable(); }
        public ImmutableArray<uint> U32s(int count) { var b = ImmutableArray.CreateBuilder<uint>(count); for (var i = 0; i < count; i++) b.Add(U32()); return b.MoveToImmutable(); }
        public ReadOnlySpan<byte> Bytes(int count) { Need(count); var span = data.AsSpan(_position, count); _position += count; return span; }
        public Vector3 Vector3() => new(F32(), F32(), F32());
        public Vector4 Vector4() => new(F32(), F32(), F32(), F32());
        public void Skip(int count) { Need(count); _position += count; }
        private void Need(int count)
        {
            if (count < 0 || _position > data.Length - count) throw new InvalidDataException("MDL file is truncated.");
        }
    }
}

public sealed class UnsupportedMdlVersionException(uint version, string message) : Exception(message)
{
    public uint Version { get; } = version;
}
