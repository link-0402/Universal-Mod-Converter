using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace AdvancedPenumbraModConverter.Core;

public sealed record CustomizationKindDescriptor(
    AssetKind Kind,
    string DisplayName,
    string Directory,
    char Prefix,
    bool SupportsEst,
    ImmutableArray<ushort> AllowedGenderRaces)
{
    public string Token(ushort modelId) => $"{Prefix}{modelId:D4}";
    public string Root(ushort genderRace, ushort modelId)
        => $"chara/human/c{genderRace:D4}/obj/{Directory}/{Token(modelId)}";

    public string Part => Kind switch
    {
        AssetKind.Hair     => "hir",
        AssetKind.Face     => "fac",
        AssetKind.Tail     => "til",
        _                  => "zer",
    };

    /// <summary>The model the game loads for this root.</summary>
    public string ModelPath(ushort genderRace, ushort modelId)
        => $"{Root(genderRace, modelId)}/model/c{genderRace:D4}{Token(modelId)}_{Part}.mdl";

    public bool SupportsRace(ushort genderRace) => AllowedGenderRaces.Contains(genderRace);
}

public readonly record struct CustomizationPathEndpoint(AssetKind Kind, ushort GenderRace, ushort ModelId);

/// <summary>
/// Which races a customization root may be converted to. Detection still accepts every
/// race a kind supports; these rules only restrict targets.
/// </summary>
public static class CustomizationTargets
{
    public static bool IsLalafell(ushort genderRace) => genderRace is 1101 or 1201;

    /// <summary>Playable race codes alternate male (c0101) and female (c0201).</summary>
    public static bool IsFemale(ushort genderRace) => genderRace / 100 % 2 == 0;

    /// <summary>
    /// Lalafell convert only among Lalafell (never to or from other races), and faces stay
    /// within the source's gender.
    /// </summary>
    public static ImmutableArray<ushort> AllowedRaces(AssetKind sourceKind, ushort sourceRace, AssetKind targetKind)
        => CustomizationKinds.Get(targetKind).AllowedGenderRaces
            .Where(race => IsLalafell(race) == IsLalafell(sourceRace))
            .Where(race => targetKind != AssetKind.Face || IsFemale(race) == IsFemale(sourceRace))
            .ToImmutableArray();

    /// <summary>Why <paramref name="targetRace"/> is not a valid target, or null.</summary>
    public static string? BlockReason(AssetKind sourceKind, ushort sourceRace, AssetKind targetKind, ushort targetRace)
    {
        if (IsLalafell(targetRace) != IsLalafell(sourceRace))
            return "Lalafell can only be converted to and from other Lalafell.";
        if (targetKind == AssetKind.Face && IsFemale(targetRace) != IsFemale(sourceRace))
            return "Faces cannot be converted between genders.";
        return AllowedRaces(sourceKind, sourceRace, targetKind).Contains(targetRace)
            ? null
            : $"{CustomizationKinds.Get(targetKind).DisplayName} is not valid for c{targetRace:D4}.";
    }
}

public static class CustomizationKinds
{
    private static readonly ImmutableArray<ushort> AllPlayable =
        Enumerable.Range(1, 18).Select(index => (ushort)(index * 100 + 1)).ToImmutableArray();

    private static readonly ImmutableDictionary<AssetKind, CustomizationKindDescriptor> Descriptors =
        new[]
        {
            new CustomizationKindDescriptor(AssetKind.Hair, "Hair", "hair", 'h', true, AllPlayable),
            new CustomizationKindDescriptor(AssetKind.Face, "Face", "face", 'f', true, AllPlayable),
            new CustomizationKindDescriptor(AssetKind.Tail, "Tail", "tail", 't', false,
                [701, 801, 1301, 1401, 1501, 1601]),
            new CustomizationKindDescriptor(AssetKind.VieraEar, "Viera Ear", "zear", 'z', false,
                [1701, 1801]),
        }.ToImmutableDictionary(descriptor => descriptor.Kind);

    public static IEnumerable<CustomizationKindDescriptor> All => Descriptors.Values;

    public static bool IsCustomization(AssetKind kind) => Descriptors.ContainsKey(kind);

    public static bool TryGet(AssetKind kind, out CustomizationKindDescriptor descriptor)
        => Descriptors.TryGetValue(kind, out descriptor!);

    public static CustomizationKindDescriptor Get(AssetKind kind)
        => TryGet(kind, out var descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a customization asset kind.");

    public static bool CanConvert(AssetKind source, AssetKind target)
        => source == target ||
           (source is AssetKind.Tail or AssetKind.VieraEar) &&
           (target is AssetKind.Tail or AssetKind.VieraEar);
}

public static partial class CustomizationPaths
{
    [GeneratedRegex(@"(?<![A-Za-z0-9])c(?<race>\d{4})(?<s1>[/\\]+)obj(?<s2>[/\\]+)(?<directory>hair|face|tail|zear)(?<s3>[/\\]+)(?<prefix>[hftz])(?<id>\d{4})(?!\d)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EndpointRegex();

    [GeneratedRegex(@"(?<prefix>mt_)?c(?<race>\d{4})h(?<id>\d{4})(?:_c(?<owner>\d{4}))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HairMaterialNameRegex();

    [GeneratedRegex(@"^/?mt_c(?<race>\d{4})b(?<body>\d{4})(?<tail>_.+\.mtrl)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SkinMaterialRegex();

    public static ImmutableArray<CustomizationPathEndpoint> FindEndpoints(string value)
    {
        if (string.IsNullOrEmpty(value)) return [];
        var endpoints = ImmutableArray.CreateBuilder<CustomizationPathEndpoint>();
        foreach (Match match in EndpointRegex().Matches(value))
        {
            if (!TryRead(match, out var endpoint)) continue;
            endpoints.Add(endpoint);
        }
        return endpoints.ToImmutable();
    }

    public static bool Contains(string value, CustomizationPathEndpoint endpoint)
        => FindEndpoints(value).Contains(endpoint);

    /// <summary>
    /// Rewrites only canonical customization roots matching <paramref name="source"/>.
    /// A race or model token elsewhere in the value is deliberately left untouched.
    /// Material paths are resolved through their material root (shared hair roots, the
    /// shared Hrothgar tail root) and receive the target's material folder layout.
    /// </summary>
    public static string Rewrite(string value, CustomizationPathEndpoint source,
        CustomizationPathEndpoint target)
    {
        if (!CustomizationKinds.CanConvert(source.Kind, target.Kind))
            throw new ArgumentException($"Unsupported customization kind conversion: {source.Kind} -> {target.Kind}.", nameof(target));

        var sourceDescriptor = CustomizationKinds.Get(source.Kind);
        var targetDescriptor = CustomizationKinds.Get(target.Kind);
        var isMaterial = Regex.IsMatch(value, @"\.mtrl(?:$|[^A-Za-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var sourceRoot = isMaterial ? GetMaterialEndpoint(source) : source;
        var targetRoot = isMaterial ? GetMaterialEndpoint(target) : target;
        var changed = false;
        var rewritten = EndpointRegex().Replace(value, match =>
        {
            if (!TryRead(match, out var endpoint) || endpoint != source && endpoint != sourceRoot)
                return match.Value;
            changed = true;
            return $"c{targetRoot.GenderRace:D4}{match.Groups["s1"].Value}obj{match.Groups["s2"].Value}" +
                   $"{CustomizationKinds.Get(targetRoot.Kind).Directory}{match.Groups["s3"].Value}" +
                   $"{CustomizationKinds.Get(targetRoot.Kind).Prefix}{targetRoot.ModelId:D4}";
        });

        // Hair materials are hard-routed by the game. TexTools resolves the
        // effective Midlander/shared material root and adds the owning race to
        // the filename so clones for different races cannot collide.
        if (isMaterial && source.Kind == AssetKind.Hair && target.Kind == AssetKind.Hair)
        {
            var before = rewritten;
            rewritten = RewriteHairMaterialName(rewritten, source, target);
            changed |= !string.Equals(before, rewritten, StringComparison.Ordinal);
        }

        if (!changed) return value;

        // Source-owned resource filenames commonly repeat the combined race/model
        // identity (for example mt_c1801z0001_...). Restrict this secondary rewrite
        // to the exact combined identity and only after a matching canonical root.
        var oldCombined = $"c{source.GenderRace:D4}{sourceDescriptor.Prefix}{source.ModelId:D4}";
        var newCombined = $"c{target.GenderRace:D4}{targetDescriptor.Prefix}{target.ModelId:D4}";
        rewritten = RewriteIdentity(rewritten, oldCombined, newCombined, source, target);
        if (isMaterial)
        {
            rewritten = RewriteSharedRootIdentity(rewritten, source, target);
            rewritten = NormalizeMaterialFolder(rewritten, source, target);
        }
        return rewritten;
    }

    /// <summary>
    /// Rewrites a reference read from a structured asset already proven to belong
    /// to the source customization. MDL string tables commonly store materials as
    /// short names such as /mt_c1801z0001_a.mtrl, without the canonical obj/zear
    /// root required by <see cref="Rewrite"/>.
    /// </summary>
    public static string RewriteOwnedReference(string value, CustomizationPathEndpoint source,
        CustomizationPathEndpoint target)
    {
        var rewritten = Rewrite(value, source, target);
        if (!string.Equals(rewritten, value, StringComparison.Ordinal)) return rewritten;

        if (source.Kind == AssetKind.Hair && target.Kind == AssetKind.Hair &&
            value.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
        {
            rewritten = RewriteHairMaterialName(value, source, target);
            if (!string.Equals(rewritten, value, StringComparison.Ordinal)) return rewritten;
        }

        var sourceDescriptor = CustomizationKinds.Get(source.Kind);
        var targetDescriptor = CustomizationKinds.Get(target.Kind);
        var oldCombined = $"c{source.GenderRace:D4}{sourceDescriptor.Prefix}{source.ModelId:D4}";
        var newCombined = $"c{target.GenderRace:D4}{targetDescriptor.Prefix}{target.ModelId:D4}";
        rewritten = RewriteIdentity(value, oldCombined, newCombined, source, target);
        return value.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase)
            ? RewriteSharedRootIdentity(rewritten, source, target)
            : rewritten;
    }

    private static string RewriteIdentity(string value, string oldCombined, string newCombined,
        CustomizationPathEndpoint source, CustomizationPathEndpoint target)
    {
        var sourcePart = source.Kind == AssetKind.VieraEar ? "zer" : "til";
        var targetPart = target.Kind == AssetKind.VieraEar ? "zer" : "til";
        return Regex.Replace(value, Regex.Escape(oldCombined) + @"(?!\d)(?:_(?<part>" + sourcePart + @")(?=_|\.|$))?",
            match => newCombined + (match.Groups["part"].Success
                ? "_" + (source.Kind != target.Kind ? targetPart : sourcePart) : ""),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Hrothgar tail materials are named after the shared t0001 root (mt_c1501t0001_...).
    /// When the material root changes they take the target's own identity, never the
    /// target root's, so they cannot replace the materials every Hrothgar tail shares.
    /// </summary>
    private static string RewriteSharedRootIdentity(string value, CustomizationPathEndpoint source,
        CustomizationPathEndpoint target)
    {
        var sourceRoot = GetMaterialEndpoint(source);
        if (source.Kind == AssetKind.Hair || sourceRoot == source || sourceRoot == GetMaterialEndpoint(target))
            return value;
        var rootCombined = $"c{sourceRoot.GenderRace:D4}{CustomizationKinds.Get(sourceRoot.Kind).Prefix}{sourceRoot.ModelId:D4}";
        var newCombined = $"c{target.GenderRace:D4}{CustomizationKinds.Get(target.Kind).Prefix}{target.ModelId:D4}";
        return RewriteIdentity(value, rootCombined, newCombined, source, target);
    }

    /// <summary>
    /// Face and ear materials live directly in material/; hair and tails use material/v0001/.
    /// Hrothgar tails keep their folder variant only when both sides are Hrothgar tails.
    /// </summary>
    private static string NormalizeMaterialFolder(string value, CustomizationPathEndpoint source,
        CustomizationPathEndpoint target)
    {
        // Same layout on both sides: keep whatever folder the mod already uses.
        if (source.Kind == target.Kind && IsHrothgarTail(source) == IsHrothgarTail(target)) return value;
        var folder = MaterialVariants(target).Length == 0 ? string.Empty : "v0001";
        return Regex.Replace(value, @"(?<sep>[/\\])material[/\\](?:v\d{4}[/\\])?",
            match => match.Groups["sep"].Value + "material" + match.Groups["sep"].Value +
                (folder.Length == 0 ? string.Empty : folder + match.Groups["sep"].Value),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>The game path a material name resolves to (first variant folder for Hrothgar tails).</summary>
    public static string MaterialPath(CustomizationPathEndpoint endpoint, string materialName)
        => MaterialPaths(endpoint, materialName)[0];

    /// <summary>
    /// Every game path a model's material name can resolve to. Hrothgar tails load their
    /// materials from the shared t0001 root in one of the variant folders v0001-v0005.
    /// </summary>
    public static string[] MaterialPaths(CustomizationPathEndpoint endpoint, string materialName)
    {
        var root = GetMaterialEndpoint(endpoint);
        var directory = CustomizationKinds.Get(root.Kind).Root(root.GenderRace, root.ModelId) + "/material";
        var name = materialName.TrimStart('/');
        var variants = MaterialVariants(endpoint);
        return variants.Length == 0
            ? [$"{directory}/{name}"]
            : variants.Select(v => $"{directory}/v{v:D4}/{name}").ToArray();
    }

    /// <summary>Material folder variants used by a customization kind (none for face and ears).</summary>
    public static int[] MaterialVariants(CustomizationPathEndpoint endpoint) => endpoint.Kind switch
    {
        AssetKind.Tail when IsHrothgarTail(endpoint) => [1, 2, 3, 4, 5],
        AssetKind.Tail or AssetKind.Hair => [1],
        _ => [],
    };

    public static bool IsHrothgarTail(CustomizationPathEndpoint endpoint)
        => endpoint.Kind == AssetKind.Tail && endpoint.GenderRace is 1501 or 1601;

    /// <summary>The root the game loads this customization's materials from.</summary>
    public static CustomizationPathEndpoint GetMaterialEndpoint(CustomizationPathEndpoint endpoint)
        => endpoint.Kind switch
        {
            AssetKind.Hair => GetHairMaterialEndpoint(endpoint),
            AssetKind.Tail when IsHrothgarTail(endpoint) => endpoint with { ModelId = 1 },
            _ => endpoint,
        };

    /// <summary>
    /// True for material roots other customizations load as well: the Midlander hair roots
    /// of styles 101-200 and the Hrothgar t0001 tail root. Their entries must be copied,
    /// never moved, or the other users lose them.
    /// </summary>
    public static bool IsSharedMaterialRoot(CustomizationPathEndpoint endpoint)
        => endpoint.Kind switch
        {
            AssetKind.Hair => endpoint.GenderRace is 101 or 201 && endpoint.ModelId is >= 101 and <= 200,
            AssetKind.Tail => IsHrothgarTail(endpoint) && endpoint.ModelId == 1,
            _ => false,
        };

    /// <summary>
    /// Updates TexTools-style skin material references after a race conversion.
    /// Roegadyn female uses Highlander-female skin; races without their own skin
    /// inherit the first skinned ancestor used by the game's race tree.
    /// </summary>
    public static string FixSkinMaterialReference(string value, ushort targetGenderRace)
    {
        var match = SkinMaterialRegex().Match(value);
        if (!match.Success) return value;
        var skinRace = GetSkinGenderRace(targetGenderRace);
        if (ushort.TryParse(match.Groups["race"].Value, out var currentSkinRace) &&
            currentSkinRace == skinRace)
            return value;
        var prefix = value.StartsWith('/') ? "/" : string.Empty;
        return $"{prefix}mt_c{skinRace:D4}b0001{match.Groups["tail"].Value}";
    }

    public static ushort GetSkinGenderRace(ushort genderRace) => genderRace switch
    {
        101 or 201 or 301 or 401 or 901 or 1101 or 1301 or 1401 or 1501 or 1601 or 1701 or 1801
            => genderRace,
        501 or 701 => 101,
        601 or 801 => 201,
        1001 => 401,
        1201 => 1101,
        _ => genderRace,
    };

    /// <summary>Returns the hard-coded material-sharing root used by hair.</summary>
    public static CustomizationPathEndpoint GetHairMaterialEndpoint(CustomizationPathEndpoint endpoint)
    {
        if (endpoint.Kind != AssetKind.Hair) return endpoint;
        if (endpoint.GenderRace is 1501 or 1601 || endpoint.ModelId < 101 || endpoint.ModelId >= 201)
            return endpoint;
        if (endpoint.ModelId < 116 && endpoint.GenderRace is 701 or 801)
            return endpoint;
        var sharedRace = (endpoint.GenderRace / 100 & 1) == 0 ? (ushort)201 : (ushort)101;
        return endpoint with { GenderRace = sharedRace };
    }

    private static string RewriteHairMaterialName(string value, CustomizationPathEndpoint source,
        CustomizationPathEndpoint target)
    {
        var sourceRoot = GetHairMaterialEndpoint(source);
        var targetRoot = GetHairMaterialEndpoint(target);
        return HairMaterialNameRegex().Replace(value, match =>
        {
            if (!ushort.TryParse(match.Groups["race"].Value, out var race) ||
                !ushort.TryParse(match.Groups["id"].Value, out var id) ||
                id != sourceRoot.ModelId || race != sourceRoot.GenderRace && race != source.GenderRace)
                return match.Value;
            if (match.Groups["owner"].Success &&
                (!ushort.TryParse(match.Groups["owner"].Value, out var owner) || owner != source.GenderRace))
                return match.Value;
            var prefix = match.Groups["prefix"].Value;
            return $"{prefix}c{targetRoot.GenderRace:D4}h{targetRoot.ModelId:D4}_c{target.GenderRace:D4}";
        });
    }

    private static bool TryRead(Match match, out CustomizationPathEndpoint endpoint)
    {
        endpoint = default;
        if (!ushort.TryParse(match.Groups["race"].Value, out var race) ||
            !ushort.TryParse(match.Groups["id"].Value, out var id)) return false;

        var directory = match.Groups["directory"].Value;
        var prefix = char.ToLowerInvariant(match.Groups["prefix"].Value[0]);
        var descriptor = CustomizationKinds.All.FirstOrDefault(candidate =>
            candidate.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase) &&
            candidate.Prefix == prefix);
        if (descriptor is null) return false;

        endpoint = new CustomizationPathEndpoint(descriptor.Kind, race, id);
        return true;
    }
}
