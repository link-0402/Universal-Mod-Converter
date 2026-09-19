using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdvancedPenumbraItemConverter.Core;
using AdvancedPenumbraItemConverter.Models;
using Dalamud.Plugin.Services;

namespace AdvancedPenumbraItemConverter.Services;

internal readonly record struct EstOverrideKey(AssetKind Kind, ushort GenderRace, ushort SetId);

internal sealed record ModResourceIndex(
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyDictionary<string, string> FileSwaps,
    IReadOnlyDictionary<EstOverrideKey, ushort> EstOverrides);

internal sealed record SkeletonResolution(
    IReadOnlyDictionary<ushort, BoneHierarchy> StepSkeletons,
    BoneHierarchy SourceSkeleton,
    IReadOnlyList<string> Warnings);

/// <summary>Resolves TexTools-compatible base and customization skeleton stacks.</summary>
internal sealed class SkeletonHierarchyService(
    GameDataService gameData,
    ISkeletonHierarchyReader hierarchyReader,
    IPluginLog log)
{
    private const ushort MidlanderMale = 101;
    private readonly Dictionary<string, BoneHierarchy> _gameCache = new(StringComparer.OrdinalIgnoreCase);

    public SkeletonResolution Resolve(HumanPbd pbd, RacialDeformationPlan route,
        CustomizationPathEndpoint source, ModResourceIndex resources)
    {
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var midlander = TryLoad(BaseSkeletonPath(MidlanderMale), resources, warnings);
        var stepSkeletons = new Dictionary<ushort, BoneHierarchy>();
        foreach (var race in route.Steps.Select(step => step.GenderRace).Distinct())
        {
            var racial = TryLoad(BaseSkeletonPath(race), resources, warnings);
            stepSkeletons[race] = racial switch
            {
                not null when midlander != null && race != MidlanderMale => racial.MergeMissing(midlander),
                not null => racial,
                null when midlander != null => midlander,
                _ => BoneHierarchy.Empty,
            };
        }

        var sourceBase = TryLoad(BaseSkeletonPath(source.GenderRace), resources, warnings)
            ?? midlander ?? BoneHierarchy.Empty;
        var extraPath = ResolveExtraSkeletonPath(pbd, source, resources, warnings);
        var extra = extraPath == null ? null : TryLoad(extraPath, resources, warnings);
        var sourceHierarchy = extra?.MergeMissing(sourceBase) ?? sourceBase;

        return new SkeletonResolution(stepSkeletons, sourceHierarchy,
            warnings.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    private BoneHierarchy? TryLoad(string gamePath, ModResourceIndex resources, ISet<string> warnings)
    {
        try
        {
            var resolvedPath = FollowFileSwaps(Normalize(gamePath), resources.FileSwaps);
            byte[]? bytes;
            if (resources.Files.TryGetValue(resolvedPath, out var localPath))
            {
                if (!File.Exists(localPath))
                    throw new FileNotFoundException("Mapped SKLB file does not exist.", localPath);
                bytes = File.ReadAllBytes(localPath);
            }
            else
            {
                if (_gameCache.TryGetValue(resolvedPath, out var cached)) return cached;
                bytes = gameData.GetRawFileBytes(resolvedPath);
                if (bytes == null) throw new FileNotFoundException("Game SKLB file was not found.", resolvedPath);
            }

            var hierarchy = hierarchyReader.Read(bytes);
            if (!resources.Files.ContainsKey(resolvedPath)) _gameCache[resolvedPath] = hierarchy;
            return hierarchy;
        }
        catch (Exception ex)
        {
            var warning = $"Skeleton '{gamePath}' could not be read; unresolved bones will remain unchanged. {ex.Message}";
            warnings.Add(warning);
            log.Warning(ex, "[APIC] {0}", warning);
            return null;
        }
    }

    private string? ResolveExtraSkeletonPath(HumanPbd pbd, CustomizationPathEndpoint source,
        ModResourceIndex resources, ISet<string> warnings)
    {
        if (source.Kind == AssetKind.VieraEar)
            return ExtraSkeletonPath(source.GenderRace, AssetKind.Face, 1);
        if (source.Kind is not (AssetKind.Hair or AssetKind.Face)) return null;

        var estPath = source.Kind == AssetKind.Hair
            ? "chara/xls/charadb/hairskeletontemplate.est"
            : "chara/xls/charadb/faceskeletontemplate.est";
        byte[]? estBytes = null;
        try
        {
            var resolvedEstPath = FollowFileSwaps(estPath, resources.FileSwaps);
            estBytes = resources.Files.TryGetValue(resolvedEstPath, out var localPath)
                ? File.ReadAllBytes(localPath)
                : gameData.GetRawFileBytes(resolvedEstPath);
        }
        catch (Exception ex)
        {
            warnings.Add($"Extra-skeleton table '{estPath}' could not be read. {ex.Message}");
        }

        var race = (ushort?)source.GenderRace;
        var seen = new HashSet<ushort>();
        while (race is { } current && seen.Add(current))
        {
            ushort skeletonId = 0;
            var overridden = resources.EstOverrides.TryGetValue(
                new EstOverrideKey(source.Kind, current, source.ModelId), out skeletonId);
            try
            {
                if (!overridden && estBytes != null)
                    _ = ExtraSkeletonTable.TryGet(estBytes, current, source.ModelId, out skeletonId);
            }
            catch (Exception ex)
            {
                warnings.Add($"Extra-skeleton table '{estPath}' is invalid. {ex.Message}");
                estBytes = null;
            }

            if (skeletonId != 0) return ExtraSkeletonPath(current, source.Kind, skeletonId);
            race = pbd.GetParentRace(current);
        }
        return null;
    }

    private static string FollowFileSwaps(string path, IReadOnlyDictionary<string, string> swaps)
    {
        var current = Normalize(path);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (swaps.TryGetValue(current, out var next))
        {
            if (!seen.Add(current)) throw new InvalidDataException($"File-swap cycle includes '{current}'.");
            current = Normalize(next);
        }
        return current;
    }

    private static string BaseSkeletonPath(ushort race)
        => $"chara/human/c{race:D4}/skeleton/base/b0001/skl_c{race:D4}b0001.sklb";

    private static string ExtraSkeletonPath(ushort race, AssetKind kind, ushort skeletonId)
    {
        var (directory, prefix) = kind switch
        {
            AssetKind.Hair => ("hair", 'h'),
            AssetKind.Face or AssetKind.VieraEar => ("face", 'f'),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        return $"chara/human/c{race:D4}/skeleton/{directory}/{prefix}{skeletonId:D4}/" +
               $"skl_c{race:D4}{prefix}{skeletonId:D4}.sklb";
    }

    internal static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
