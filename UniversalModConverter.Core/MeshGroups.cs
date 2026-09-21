using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace UniversalModConverter.Core;

/// <summary>One part of a mesh group (an MDL submesh).</summary>
public sealed record MdlMeshPart(int Index, int Triangles, ImmutableArray<string> Attributes);

/// <summary>One mesh of a model's highest-detail LOD, as TexTools calls it: a mesh group.</summary>
public sealed record MdlMeshGroup(int Index, string Material, int Triangles, int Vertices, int Parts,
    ImmutableArray<string> Attributes, bool IsSkin, ImmutableArray<MdlMeshPart> PartList);

/// <summary>A part of a mesh group, by group and part index within LOD 0.</summary>
public readonly record struct MeshPartRef(int Group, int Part);

/// <summary>The mesh groups and single parts a user chose to drop from one output model.</summary>
/// <param name="ExpectedGroupCount">Mesh-group count seen at preview; guards against a changed file.</param>
/// <param name="Parts">Parts removed from groups that stay; a group losing every part is in <paramref name="Groups"/>.</param>
public sealed record MeshRemoval(ImmutableArray<int> Groups, int ExpectedGroupCount, ImmutableArray<MeshPartRef> Parts = default)
{
    public ImmutableArray<MeshPartRef> PartsOrEmpty => Parts.IsDefault ? [] : Parts;
}

public static class MdlMeshGroups
{
    /// <summary>Lists the LOD 0 meshes of an MDL v6 file.</summary>
    public static IReadOnlyList<MdlMeshGroup> Describe(byte[] data)
    {
        var model = MdlFile.Read(data);
        var lod = model.Lods[0];
        var groups = new List<MdlMeshGroup>(lod.MeshCount);
        for (var g = 0; g < lod.MeshCount; g++)
        {
            var mesh = model.Meshes[lod.MeshIndex + g];
            var material = mesh.MaterialIndex < model.Materials.Length ? model.Materials[mesh.MaterialIndex] : "?";
            var mask = 0u;
            var parts = ImmutableArray.CreateBuilder<MdlMeshPart>();
            for (var s = mesh.SubmeshIndex; s < mesh.SubmeshIndex + mesh.SubmeshCount && s < model.Submeshes.Length; s++)
            {
                var submesh = model.Submeshes[s];
                mask |= submesh.AttributeMask;
                parts.Add(new MdlMeshPart(s - mesh.SubmeshIndex, (int)(submesh.IndexCount / 3), Names(submesh.AttributeMask)));
            }
            groups.Add(new MdlMeshGroup(g, material, (int)(mesh.IndexCount / 3), mesh.VertexCount,
                mesh.SubmeshCount, Names(mask), ResourceReferences.IsSkinMaterial(material), parts.ToImmutable()));
        }
        return groups;

        ImmutableArray<string> Names(uint mask)
            => Enumerable.Range(0, Math.Min(32, model.Attributes.Length))
                .Where(bit => (mask & (1u << bit)) != 0)
                .Select(bit => model.Attributes[bit])
                .ToImmutableArray();
    }

    /// <summary>
    /// The model with the removed mesh groups and parts hidden in place rather than taken out,
    /// so nothing is renumbered: used to show the user their choices on a character wearing the
    /// source item. Lower LODs hide the matching mesh or part, as <see cref="Remove"/> does.
    /// </summary>
    public static byte[] Hide(byte[] data, MeshRemoval removal)
    {
        var model = MdlFile.Read(data);
        if (model.Lods[0].MeshCount != removal.ExpectedGroupCount)
            throw new InvalidDataException(
                $"The model has {model.Lods[0].MeshCount} mesh group(s), but {removal.ExpectedGroupCount} were previewed.");
        var submeshes = PartSubmeshes(model, removal.PartsOrEmpty.Where(p => !removal.Groups.Contains(p.Group)));
        foreach (var group in removal.Groups)
            foreach (var mesh in MatchingMeshes(model, group))
                for (var p = 0; p < model.Meshes[mesh].SubmeshCount; p++)
                    submeshes.Add(model.Meshes[mesh].SubmeshIndex + p);
        model.RemoveSubmeshes(submeshes);
        return model.Write();
    }

    /// <summary>The LOD 0 mesh of <paramref name="group"/> and the mesh at its place in lower LODs with the same material.</summary>
    private static List<int> MatchingMeshes(MdlFile model, int group)
    {
        var lod0 = model.Lods[0];
        if ((uint)group >= lod0.MeshCount) throw new InvalidDataException($"Mesh group {group} does not exist in the model.");
        var material = model.Meshes[lod0.MeshIndex + group].MaterialIndex;
        var result = new List<int> { lod0.MeshIndex + group };
        for (var l = 1; l < model.Header.LodCount; l++)
        {
            var lod = model.Lods[l];
            if (group < lod.MeshCount && model.Meshes[lod.MeshIndex + group].MaterialIndex == material)
                result.Add(lod.MeshIndex + group);
        }
        return result;
    }

    /// <summary>Absolute submesh indices of single parts, in LOD 0 and in matching lower-LOD meshes with as many parts.</summary>
    private static HashSet<int> PartSubmeshes(MdlFile model, IEnumerable<MeshPartRef> parts)
    {
        var lod0 = model.Lods[0];
        var submeshes = new HashSet<int>();
        foreach (var (group, part) in parts)
        {
            if ((uint)group >= lod0.MeshCount)
                throw new InvalidDataException($"Mesh group {group} does not exist in the model.");
            var mesh = model.Meshes[lod0.MeshIndex + group];
            if ((uint)part >= mesh.SubmeshCount)
                throw new InvalidDataException($"Mesh group {group} has no part {part}.");
            foreach (var index in MatchingMeshes(model, group))
                if (model.Meshes[index].SubmeshCount == mesh.SubmeshCount)
                    submeshes.Add(model.Meshes[index].SubmeshIndex + part);
        }
        return submeshes;
    }

    /// <summary>
    /// Removes LOD 0 mesh groups (and the matching mesh of lower LODs, when one exists at the
    /// same position with the same material) and returns the rebuilt file.
    /// </summary>
    public static byte[] Remove(byte[] data, MeshRemoval removal)
    {
        var model = MdlFile.Read(data);
        var lod0 = model.Lods[0];
        if (lod0.MeshCount != removal.ExpectedGroupCount)
            throw new InvalidDataException(
                $"The model has {lod0.MeshCount} mesh group(s), but {removal.ExpectedGroupCount} were previewed.");

        // Single parts first: they are hidden in place, before any mesh is renumbered.
        model.RemoveSubmeshes(PartSubmeshes(model, removal.PartsOrEmpty.Where(p => !removal.Groups.Contains(p.Group))));

        var absolute = new HashSet<int>();
        foreach (var group in removal.Groups)
        {
            if ((uint)group >= lod0.MeshCount)
                throw new InvalidDataException($"Mesh group {group} does not exist in the model.");
            var material = model.Meshes[lod0.MeshIndex + group].MaterialIndex;
            absolute.Add(lod0.MeshIndex + group);
            for (var l = 1; l < model.Header.LodCount; l++)
            {
                var lod = model.Lods[l];
                if (group < lod.MeshCount && model.Meshes[lod.MeshIndex + group].MaterialIndex == material)
                    absolute.Add(lod.MeshIndex + group);
            }
        }

        model.RemoveMeshes(absolute);
        // A removed part's material would still be loaded, and for a skin part moved to an
        // accessory it cannot be found at all, which keeps the whole model from showing.
        model.RemoveUnusedMaterials();
        return model.Write();
    }
}

/// <summary>A model the output mod will ship for the target item.</summary>
/// <param name="Local">Mod-relative file path in the output mod.</param>
/// <param name="EditError">Why mesh groups of this model cannot be removed, or null.</param>
/// <param name="SourceFile">The mod file it was converted from, with the same parts; null for a game copy.</param>
/// <param name="SourceGamePaths">The source item's game paths it replaces, for the preview on a character.</param>
public sealed record GearOutputModel(string Local, ImmutableArray<string> GamePaths, ImmutableArray<string> Options,
    ushort? GenderRace, IReadOnlyList<MdlMeshGroup> Groups, string? EditError,
    string? SourceFile = null, ImmutableArray<string> SourceGamePaths = default)
{
    public bool Editable => EditError == null && Groups.Count > 0;
}

public static partial class GearOutputModels
{
    [GeneratedRegex(@"c(?<race>\d{4})[ea]\d{4}_", RegexOptions.CultureInvariant)]
    private static partial Regex RaceRegex();

    /// <summary>
    /// Lists the target models of the planned output with the bytes they will have, read
    /// from the planned operations (or the mod, for files an in-place plan leaves alone).
    /// </summary>
    public static List<GearOutputModel> Collect(GearConversionPlan plan, string modDirectory)
    {
        var prefix = plan.Request.Target.Root + "/model/";
        var targets = new Dictionary<string, (List<string> Keys, List<string> Options, string Local)>(StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var otherUses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in plan.Result.Containers)
        foreach (var (key, local) in container.FileEntries())
        {
            var path = GamePath.Normalize(key);
            var normalized = GamePath.NormalizeLocal(local);
            if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(".mdl", StringComparison.Ordinal))
            {
                otherUses.Add(normalized);
                continue;
            }
            if (!targets.TryGetValue(normalized, out var entry))
                targets[normalized] = entry = (new List<string>(), new List<string>(), GamePath.ToLocal(local));
            entry.Keys.Add(path);
            if (!entry.Options.Contains(container.Label)) entry.Options.Add(container.Label);
            if (plan.ModelSources.TryGetValue((container.Address, path), out var source)) sources.TryAdd(normalized, source);
        }

        // The last operation for a destination decides its content.
        var operations = new Dictionary<string, PlannedFileOperation>(StringComparer.Ordinal);
        foreach (var operation in plan.Files)
            if (operation.Operation != LocalFileOperation.Delete)
                operations[GamePath.NormalizeLocal(operation.Destination)] = operation;

        var result = new List<GearOutputModel>();
        foreach (var (normalized, (keys, options, local)) in targets.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            var race = RaceRegex().Match(keys[0]) is { Success: true } m ? ushort.Parse(m.Groups["race"].Value) : (ushort?)null;
            IReadOnlyList<MdlMeshGroup> groups = [];
            string? error = null;
            try
            {
                var bytes = operations.TryGetValue(normalized, out var operation)
                    ? operation.Content ?? File.ReadAllBytes(PathSafety.ResolveRelative(modDirectory, operation.Source!))
                    : File.ReadAllBytes(PathSafety.ResolveRelative(modDirectory, local));
                groups = MdlMeshGroups.Describe(bytes);
                if (otherUses.Contains(normalized))
                    error = "This file is also used for other items in the mod, so it cannot be edited here.";
            }
            catch (UnsupportedMdlVersionException ex) when (ex.Version == MdlFile.Version5)
            {
                error = "MDL version 5 models cannot be edited. Re-export the model with a current tool.";
            }
            catch (Exception ex)
            {
                error = $"The model cannot be read: {ex.Message}";
            }
            var sourcePaths = keys
                .SelectMany(k => plan.GamePathMap.Where(p => string.Equals(p.Value, k, StringComparison.Ordinal)).Select(p => p.Key))
                .Distinct(StringComparer.Ordinal)
                .ToImmutableArray();
            result.Add(new GearOutputModel(local, keys.ToImmutableArray(), options.ToImmutableArray(), race, groups, error,
                sources.GetValueOrDefault(normalized), sourcePaths));
        }
        return result;
    }

    /// <summary>Rewrites the models of a written (staged) output without the removed mesh groups.</summary>
    public static void ApplyRemovals(string outputDirectory, IReadOnlyDictionary<string, MeshRemoval> removals,
        Action<string>? log = null)
    {
        foreach (var (local, removal) in removals)
        {
            if (removal.Groups.IsDefaultOrEmpty && removal.PartsOrEmpty.IsEmpty) continue;
            var path = PathSafety.ResolveRelative(outputDirectory, local);
            File.WriteAllBytes(path, MdlMeshGroups.Remove(File.ReadAllBytes(path), removal));
            var what = new List<string>();
            if (!removal.Groups.IsDefaultOrEmpty) what.Add($"mesh group(s) {string.Join(", ", removal.Groups.Order())}");
            if (!removal.PartsOrEmpty.IsEmpty)
                what.Add($"part(s) {string.Join(", ", removal.PartsOrEmpty.Select(p => $"{p.Group}.{p.Part}"))}");
            log?.Invoke($"Removed {string.Join(" and ", what)} from {local}");
        }
    }
}
