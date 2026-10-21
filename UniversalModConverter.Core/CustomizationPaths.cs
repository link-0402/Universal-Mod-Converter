using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace UniversalModConverter.Core;

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
        AssetKind.Body     => "top",
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
    /// within the source's gender. Skins are only offered for the races the game loads a skin
    /// for (Elezen and Miqo'te use the Midlander one, for example), plus the source's own.
    /// </summary>
    public static ImmutableArray<ushort> AllowedRaces(AssetKind sourceKind, ushort sourceRace, AssetKind targetKind)
        => CustomizationKinds.Get(targetKind).AllowedGenderRaces
            .Where(race => IsLalafell(race) == IsLalafell(sourceRace))
            .Where(race => targetKind is not (AssetKind.Face or AssetKind.Body) ||
                           IsFemale(race) == IsFemale(sourceRace))
            .Where(race => targetKind != AssetKind.Body || race == sourceRace ||
                           CustomizationPaths.GetSkinGenderRace(race) == race)
            .ToImmutableArray();

    /// <summary>Why <paramref name="targetRace"/> is not a valid target, or null.</summary>
    public static string? BlockReason(AssetKind sourceKind, ushort sourceRace, AssetKind targetKind, ushort targetRace)
    {
        if (IsLalafell(targetRace) != IsLalafell(sourceRace))
            return "Lalafell can only be converted to and from other Lalafell.";
        if (targetKind is AssetKind.Face or AssetKind.Body && IsFemale(targetRace) != IsFemale(sourceRace))
            return targetKind == AssetKind.Body
                ? "Skin textures cannot be converted between genders: the bodies are shaped differently."
                : "Faces cannot be converted between genders.";
        if (targetKind == AssetKind.Body && targetRace != sourceRace &&
            CustomizationPaths.GetSkinGenderRace(targetRace) is var skin && skin != targetRace)
            return $"{RaceNames.Name(targetRace)} wears the {RaceNames.Name(skin)} skin; choose {RaceNames.Name(skin)} instead.";
        return AllowedRaces(sourceKind, sourceRace, targetKind).Contains(targetRace)
            ? null
            : $"{CustomizationKinds.Get(targetKind).DisplayName} is not valid for {RaceNames.Describe(targetRace)}.";
    }
}

public static class CustomizationKinds
{
    private static readonly ImmutableDictionary<AssetKind, CustomizationKindDescriptor> Descriptors =
        new[]
        {
            new CustomizationKindDescriptor(AssetKind.Hair, "Hair", "hair", 'h', true, GenderRaces.Playable),
            new CustomizationKindDescriptor(AssetKind.Face, "Face", "face", 'f', true, GenderRaces.Playable),
            new CustomizationKindDescriptor(AssetKind.Tail, "Tail", "tail", 't', false,
                [701, 801, 1301, 1401, 1501, 1601]),
            new CustomizationKindDescriptor(AssetKind.VieraEar, "Viera Ear", "zear", 'z', false,
                [1701, 1801]),
            new CustomizationKindDescriptor(AssetKind.Body, "Skin", "body", 'b', false, GenderRaces.Playable),
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
    [GeneratedRegex(@"(?<![A-Za-z0-9])c(?<race>\d{4})(?<s1>[/\\]+)obj(?<s2>[/\\]+)(?<directory>hair|face|tail|zear|body)(?<s3>[/\\]+)(?<prefix>[hftzb])(?<id>\d{4})(?!\d)",
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
    /// Whether <paramref name="value"/> names a path of the customization: one under its own root,
    /// and for a second clan's entry (Xaela face 101, tail 103) the model it loads from the first
    /// clan's root and the materials it takes from there (see <see cref="Owner"/>).
    /// </summary>
    public static bool Owns(string value, CustomizationPathEndpoint endpoint)
        => Occurrences(value).Any(found => Loads(found, endpoint));

    /// <summary>
    /// Whether converting <paramref name="source"/> to <paramref name="target"/> moves a path
    /// <paramref name="value"/> names: one the source owns, except its model when both load the
    /// same one (Raen face 1 to Xaela face 101 moves only the materials).
    /// </summary>
    public static bool Moves(string value, CustomizationPathEndpoint source, CustomizationPathEndpoint target)
        => Occurrences(value).Any(found => Loads(found, source) && (found.IsMaterial || Owner(source) != Owner(target)));

    /// <summary>A customization root named in a value, with what follows it: its material or texture folder, and the file name.</summary>
    private readonly record struct Occurrence(CustomizationPathEndpoint Root, bool IsMaterial, string Name);

    [GeneratedRegex(@"\G[/\\]+(?<folder>[A-Za-z]+)?[^""'\s,;]*", RegexOptions.CultureInvariant)]
    private static partial Regex RestOfPathRegex();

    private static IEnumerable<Occurrence> Occurrences(string value)
    {
        if (string.IsNullOrEmpty(value)) yield break;
        foreach (Match match in EndpointRegex().Matches(value))
        {
            if (!TryRead(match, out var endpoint)) continue;
            var rest = RestOfPathRegex().Match(value, match.Index + match.Length);
            var folder = rest.Success ? rest.Groups["folder"].Value : string.Empty;
            var path = rest.Success ? rest.Value : string.Empty;
            // Textures anywhere under the root (common/texture/eye/ too) go with the materials.
            yield return new Occurrence(endpoint,
                folder.Equals("material", StringComparison.OrdinalIgnoreCase) || folder.Equals("texture", StringComparison.OrdinalIgnoreCase) ||
                MaterialPathRegex().IsMatch(path),
                path[(path.LastIndexOfAny(['/', '\\']) + 1)..]);
        }
    }

    /// <summary>
    /// Whether a path under <paramref name="found"/> belongs to <paramref name="entry"/>: everything
    /// under its own root, and from a second clan's model root the model and the materials it
    /// takes from there. A model under a second clan's own root is one the game never loads.
    /// </summary>
    private static bool Loads(Occurrence found, CustomizationPathEndpoint entry)
        => Owner(entry) == entry
            ? found.Root == entry
            : found.Root == entry && found.IsMaterial ||
              found.Root == Owner(entry) && (!found.IsMaterial || TakesFromFirstClan(entry, found.Name));

    /// <summary>
    /// Au Ra tails load their model from tN for both clans, but their material from tN for Raen
    /// and from t(N+100) for Xaela, a root that holds nothing else. Faces of the races whose second
    /// clan has faces of its own do the same (face 101 is the second clan's face 1): Xaela, The
    /// Lost and Veena load the model of fN and the materials of f(N+100), except that Veena take
    /// their face skin (_fac) and The Lost their eye area (_etc) from fN. Each clan's materials
    /// are a customization of their own (Xaela tail 103 next to Raen tail 3): converting one
    /// moves the shared model and that clan's materials only.
    /// </summary>
    private const ushort ClanOffset = 100;

    public static bool IsAuRaTail(CustomizationPathEndpoint endpoint)
        => endpoint.Kind == AssetKind.Tail && endpoint.GenderRace is 1301 or 1401;

    /// <summary>Whether the second clan of the race loads this kind's materials from a root of its own.</summary>
    private static bool HasClanRoots(CustomizationPathEndpoint endpoint)
        => IsAuRaTail(endpoint) ||
           endpoint.Kind == AssetKind.Face && endpoint.GenderRace is 1301 or 1401 or 1501 or 1601 or 1701 or 1801;

    /// <summary>The second clan of a race whose faces or tails have a material root of their own: Xaela, Lost, Veena.</summary>
    public static string SecondClan(ushort genderRace) => genderRace switch
    {
        1301 or 1401 => "Xaela",
        1501 or 1601 => "Lost",
        _ => "Veena",
    };

    /// <summary>
    /// The material root of the race's second clan (an Au Ra tail 3's Xaela root t0103, a Viera
    /// face 1's Veena root f0101), or null for a root without one.
    /// </summary>
    public static CustomizationPathEndpoint? ClanMaterialEndpoint(CustomizationPathEndpoint endpoint)
        => HasClanRoots(endpoint) && endpoint.ModelId is > 0 and < ClanOffset
            ? endpoint with { ModelId = (ushort)(endpoint.ModelId + ClanOffset) }
            : null;

    /// <summary>
    /// The customization a root belongs to: itself, except that a second clan's material root
    /// (t0103, f0101) belongs to the tail or face whose model loads it (t0003, f0001).
    /// </summary>
    public static CustomizationPathEndpoint Owner(CustomizationPathEndpoint endpoint)
        => HasClanRoots(endpoint) && endpoint.ModelId is > ClanOffset and < 2 * ClanOffset
            ? endpoint with { ModelId = (ushort)(endpoint.ModelId - ClanOffset) }
            : endpoint;

    /// <summary>The first clan of a race whose faces or tails have a material root for the second: Raen, Hellsguard, Rava.</summary>
    public static string FirstClan(ushort genderRace) => genderRace switch
    {
        1301 or 1401 => "Raen",
        1501 or 1601 => "Hellsguard",
        _ => "Rava",
    };

    /// <summary>
    /// The clan whose materials a customization is, for one of a race with a root per clan
    /// ("Xaela" for Au Ra tail 103, "Raen" for tail 3), or null for any other.
    /// </summary>
    public static string? ClanOf(CustomizationPathEndpoint endpoint)
        => Owner(endpoint) != endpoint ? SecondClan(endpoint.GenderRace)
            : ClanMaterialEndpoint(endpoint) != null ? FirstClan(endpoint.GenderRace)
            : null;

    /// <summary>
    /// The other clan's customization loading the same model (Raen tail 3 for Xaela tail 103 and
    /// the other way round), or null for one no other clan shares.
    /// </summary>
    public static CustomizationPathEndpoint? OtherClan(CustomizationPathEndpoint endpoint)
        => Owner(endpoint) is var owner && owner != endpoint ? owner : ClanMaterialEndpoint(endpoint);

    /// <summary>
    /// The part of the name (after its first 13 characters) a second clan's face loads from the
    /// first clan's root instead of its own: Veena their face skin, The Lost their eye area.
    /// </summary>
    private static string? FirstClanPart(CustomizationPathEndpoint clanRoot)
        => clanRoot.Kind != AssetKind.Face ? null
            : clanRoot.GenderRace is 1701 or 1801 ? "_fac"
            : clanRoot.GenderRace is 1501 or 1601 ? "_etc"
            : null;

    /// <summary>Whether a second clan's <paramref name="entry"/> loads the material <paramref name="name"/> from the first clan's root.</summary>
    private static bool TakesFromFirstClan(CustomizationPathEndpoint entry, string name)
        => Owner(entry) != entry && FirstClanPart(entry) is { } part && name.Length > IdentityLength &&
           name.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) &&
           name[IdentityLength..].StartsWith(part, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Where each clan of the target loads its material from, paired with the root the source
    /// loads it from: the target itself, and the other clan loading the same model, which needs
    /// the material where the game has none of its own (the caller checks).
    /// </summary>
    public static IReadOnlyList<(CustomizationPathEndpoint Source, CustomizationPathEndpoint Target)> MaterialClans(
        CustomizationPathEndpoint source, CustomizationPathEndpoint target)
        => OtherClan(target) is { } other
            ? [(source, target), (source, other)]
            : [(source, target)];

    [GeneratedRegex(@"[/\\](?:material|texture)[/\\]|\.(?:mtrl|tex)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MaterialPathRegex();

    /// <summary>
    /// Rewrites only canonical customization roots matching <paramref name="source"/>.
    /// A race or model token elsewhere in the value is deliberately left untouched.
    /// Material paths are resolved through their material root (shared hair roots, the
    /// shared Hrothgar tail root) and receive the target's material folder layout.
    /// For a clan's customization (see <see cref="ClanOffset"/>) the model goes from the source's
    /// model root to the target's, and the materials and textures from the source's own root to
    /// the target's; nothing of the other clan moves.
    /// </summary>
    public static string Rewrite(string value, CustomizationPathEndpoint source,
        CustomizationPathEndpoint target)
        => RewriteClans(value, source, target, RewriteRoot);

    /// <summary>
    /// <paramref name="rewrite"/> from source to target, with the roots of the model or of the
    /// materials when they differ (see <see cref="Rewrite"/>).
    /// </summary>
    private static string RewriteClans(string value, CustomizationPathEndpoint source, CustomizationPathEndpoint target,
        Func<string, CustomizationPathEndpoint, CustomizationPathEndpoint, string> rewrite)
    {
        if (Owner(source) == source && Owner(target) == target) return rewrite(value, source, target);
        if (!MaterialPathRegex().IsMatch(value)) return rewrite(value, Owner(source), Owner(target));
        var name = value[(value.LastIndexOfAny(['/', '\\']) + 1)..];
        var to = TakesFromFirstClan(target, name) ? Owner(target) : target;
        // Veena's face skin and The Lost's eye area are named after the first clan's root, where they load from.
        var from = TakesFromFirstClan(source, name) && !Contains(value, source) &&
                   !name.Contains(Identity(source), StringComparison.OrdinalIgnoreCase)
            ? Owner(source)
            : source;
        return rewrite(value, from, to);
    }

    private static string RewriteRoot(string value, CustomizationPathEndpoint source,
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
        var hairMaterial = isMaterial && source.Kind == AssetKind.Hair && target.Kind == AssetKind.Hair;
        if (hairMaterial)
        {
            var before = rewritten;
            rewritten = RewriteHairMaterialName(rewritten, source, target);
            changed |= !string.Equals(before, rewritten, StringComparison.Ordinal);
        }

        if (!changed) return value;

        // Source-owned resource filenames commonly repeat the combined race/model
        // identity (for example mt_c1801z0001_...). Restrict this secondary rewrite
        // to the exact combined identity and only after a matching canonical root.
        // A hair material's name is already the target's: the game finds its folder from
        // the material root it starts with, which is the source's own identity when a
        // Midlander hair keeps its ID (mt_c0101h0111_c1701_...), so it must stay.
        var oldCombined = $"c{source.GenderRace:D4}{sourceDescriptor.Prefix}{source.ModelId:D4}";
        var newCombined = $"c{target.GenderRace:D4}{targetDescriptor.Prefix}{target.ModelId:D4}";
        if (!hairMaterial)
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
        // Read as the source's model names it.
        return RewriteClans(AsGameReadsIt(value, source), source, target, RewriteOwnedRoot);
    }

    private static string RewriteOwnedRoot(string value, CustomizationPathEndpoint source,
        CustomizationPathEndpoint target)
    {
        var rewritten = RewriteRoot(value, source, target);
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

    /// <summary>The combined race/model identity resource filenames repeat, e.g. c1401t0003.</summary>
    private static string Identity(CustomizationPathEndpoint endpoint)
        => $"c{endpoint.GenderRace:D4}{CustomizationKinds.Get(endpoint.Kind).Prefix}{endpoint.ModelId:D4}";

    /// <summary>The length of <c>mt_</c> and an identity: the characters of a material name the game replaces.</summary>
    private const int IdentityLength = 13;

    /// <summary>
    /// Whether the game names this material of <paramref name="root"/> after the root itself, writing
    /// <c>mt_{identity}</c> over its first 13 characters: faces, tails and ears do, for a name that
    /// starts like one of the same kind (a skin material keeps its own).
    /// </summary>
    private static bool HasGameIdentity(CustomizationPathEndpoint root, string name)
        => root.Kind is AssetKind.Face or AssetKind.Tail or AssetKind.VieraEar &&
           name.Length > IdentityLength && GameIdentityRegex().Match(name) is { Success: true } match &&
           char.ToLowerInvariant(match.Groups["prefix"].Value[0]) == CustomizationKinds.Get(root.Kind).Prefix;

    [GeneratedRegex(@"^mt_c\d{4}(?<prefix>[ftz])\d{4}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GameIdentityRegex();

    /// <summary>
    /// A model's short material name with the identity the game reads it as: that of the root the
    /// source loads its materials from. A face, tail or ear model may name its material after
    /// another (a c1401t0002 model naming /mt_c1401t0001_a.mtrl loads mt_c1401t0002_a.mtrl); said
    /// as the game reads it, the name converts like any other.
    /// </summary>
    private static string AsGameReadsIt(string value, CustomizationPathEndpoint source)
    {
        var slash = value.StartsWith('/') ? "/" : string.Empty;
        var name = value[slash.Length..];
        var root = GetMaterialEndpoint(source);
        if (name.Contains('/') || !name.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase) || !HasGameIdentity(root, name))
            return value;
        return $"{slash}mt_{Identity(root)}{name[IdentityLength..]}";
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
    /// When the material root changes they take the target's own identity. For a Hrothgar tail
    /// target that is the shared root's, since the game writes mt_c{race}t0001 over the first
    /// 13 characters whatever the model says, followed by the tail's own ID, so they cannot
    /// replace the materials every Hrothgar tail shares (mt_c1601t0001_t0003_a.mtrl).
    /// </summary>
    private static string RewriteSharedRootIdentity(string value, CustomizationPathEndpoint source,
        CustomizationPathEndpoint target)
    {
        var sourceRoot = GetMaterialEndpoint(source);
        var targetRoot = GetMaterialEndpoint(target);
        if (source.Kind == AssetKind.Hair || sourceRoot == targetRoot) return value;
        var newCombined = Identity(target);
        if (sourceRoot != source) value = RewriteIdentity(value, Identity(sourceRoot), newCombined, source, target);
        if (!IsHrothgarTail(target)) return value;
        var token = CustomizationKinds.Get(target.Kind).Token(target.ModelId);
        return Regex.Replace(value, @"(?<=mt_)" + Regex.Escape(newCombined) + @"(?!\d)(?!_" + token + ")",
            $"{Identity(targetRoot)}_{token}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Face and ear materials live directly in material/; hair and tails use material/v0001/.
    /// Hrothgar tails keep their folder variant only when both sides are Hrothgar tails.
    /// </summary>
    private static string NormalizeMaterialFolder(string value, CustomizationPathEndpoint source,
        CustomizationPathEndpoint target)
    {
        // Same layout on both sides: keep whatever folder the mod already uses.
        if (source.Kind == target.Kind && HasFurFolders(source) == HasFurFolders(target)) return value;
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
    /// For an Au Ra tail's Xaela material root, the material carries that root's name
    /// (/mt_c1401t0003_a.mtrl loads t0103/material/v0001/mt_c1401t0103_a.mtrl).
    /// A model may name its material by a complete game path instead (the same test gear
    /// conversion uses); that path is the material, with no folder added to it.
    /// A hair loading the shared Midlander root also gets that root's race in the name: the game
    /// writes it over the four digits after mt_c (/mt_c1701h0111_x.mtrl on Viera male hair 111
    /// loads c0101/obj/hair/h0111/material/v0001/mt_c0101h0111_x.mtrl), and keeps the rest.
    /// </summary>
    public static string[] MaterialPaths(CustomizationPathEndpoint endpoint, string materialName)
    {
        var named = materialName.Replace('\\', '/').TrimStart('/');
        if (named.Contains('/')) return CompletePaths(endpoint, named);

        var root = GetMaterialEndpoint(endpoint);
        var name = materialName.TrimStart('/');
        if (HasGameIdentity(root, name))
        {
            // The game writes the root's identity over the first 13 characters of a face, tail or
            // ear material's name, whatever they say; a second clan's face takes one part from
            // the first clan's root.
            if (FirstClanPart(root) is { } part && Owner(root) != root &&
                name[IdentityLength..].StartsWith(part, StringComparison.OrdinalIgnoreCase))
                root = Owner(root);
            name = $"mt_{Identity(root)}{name[IdentityLength..]}";
        }
        var directory = CustomizationKinds.Get(root.Kind).Root(root.GenderRace, root.ModelId) + "/material";
        if (root.Kind == AssetKind.Hair && IsSharedMaterialRoot(root) &&
            name.Length > 8 && name.StartsWith("mt_c", StringComparison.OrdinalIgnoreCase))
            name = $"{name[..4]}{root.GenderRace:D4}{name[8..]}";
        var variants = MaterialVariants(endpoint);
        return variants.Length == 0
            ? [$"{directory}/{name}"]
            : variants.Select(v => $"{directory}/v{v:D4}/{name}").ToArray();
    }

    [GeneratedRegex(@"(?<pre>/material/v)\d{4}(?=/)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VariantFolderRegex();

    [GeneratedRegex(@"^j_ex_h(?<id>\d{4})_", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HairPhysicsBoneRegex();

    /// <summary>
    /// The hair skeletons a model's physics bones belong to: each hair skeleton names its bones
    /// after itself (j_ex_h0108_ke_b is a bone of skeleton h0108), so the model only moves right
    /// on a character whose hair loads that skeleton.
    /// </summary>
    public static IReadOnlyList<ushort> HairSkeletons(IEnumerable<string> modelStrings)
        => modelStrings.Select(s => HairPhysicsBoneRegex().Match(s))
            .Where(m => m.Success)
            .Select(m => ushort.Parse(m.Groups["id"].Value))
            .Distinct()
            .Order()
            .ToArray();

    /// <summary>
    /// The paths a material the model names by its complete game path resolves to. An Au Ra
    /// Xaela root loads the same material from its own root, so the path moves to it; a
    /// Hrothgar tail loads it from any variant folder, the named one first.
    /// </summary>
    private static string[] CompletePaths(CustomizationPathEndpoint endpoint, string path)
    {
        if (Owner(endpoint) is var owner && owner != endpoint)
            path = Rewrite(path, owner, endpoint);
        var variants = MaterialVariants(endpoint);
        if (variants.Length < 2 || !VariantFolderRegex().IsMatch(path)) return [path];
        return variants.Select(v => VariantFolderRegex().Replace(path, m => m.Groups["pre"].Value + v.ToString("D4"), 1))
            .Prepend(path).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Material folder variants used by a customization kind (none for face and ears).</summary>
    public static int[] MaterialVariants(CustomizationPathEndpoint endpoint) => endpoint.Kind switch
    {
        _ when HasFurFolders(endpoint) => [1, 2, 3, 4, 5],
        AssetKind.Tail or AssetKind.Hair or AssetKind.Body => [1],
        _ => [],
    };

    public static bool IsHrothgarTail(CustomizationPathEndpoint endpoint)
        => endpoint.Kind == AssetKind.Tail && endpoint.GenderRace is 1501 or 1601;

    /// <summary>
    /// Hrothgar skins and tails keep their materials in five folders, v0001-v0005, one per fur
    /// pattern, which picks the folder the game loads.
    /// </summary>
    public static bool HasFurFolders(CustomizationPathEndpoint endpoint)
        => endpoint.Kind is AssetKind.Tail or AssetKind.Body && endpoint.GenderRace is 1501 or 1601;

    /// <summary>
    /// The same material path in every other fur folder of <paramref name="target"/>, for a target
    /// that has them: each fur pattern loads its own folder, so a material given to one needs all.
    /// </summary>
    public static IEnumerable<string> OtherFurFolders(string path, CustomizationPathEndpoint target)
    {
        if (!HasFurFolders(target) || VariantFolderRegex().Match(path) is not { Success: true } match) yield break;
        var current = int.Parse(path.AsSpan(match.Index + match.Groups["pre"].Length, 4));
        foreach (var variant in MaterialVariants(target).Where(v => v != current))
            yield return VariantFolderRegex().Replace(path, m => m.Groups["pre"].Value + variant.ToString("D4"), 1);
    }

    /// <summary>The fur folder of a material path (v0003 is 3), or null for a path without one.</summary>
    public static int? MaterialFolder(string path)
        => VariantFolderRegex().Match(path) is { Success: true } match
            ? int.Parse(path.AsSpan(match.Index + match.Groups["pre"].Length, 4))
            : null;

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
        // A body the target's skin race has too (b0091) is kept; any other becomes its body 1.
        var body = ushort.TryParse(match.Groups["body"].Value, out var id) && SkinBodiesInGame(skinRace).Contains(id) ? id : (ushort)1;
        var prefix = value.StartsWith('/') ? "/" : string.Empty;
        return $"{prefix}mt_c{skinRace:D4}b{body:D4}{match.Groups["tail"].Value}";
    }

    /// <summary>Every body ID the game has a skin material for, per skin race (b0091 and the like beside the players' own).</summary>
    private static ushort[] SkinBodiesInGame(ushort skinRace) => skinRace switch
    {
        101 => [1, 4, 91, 250],
        201 or 901 => [1, 91],
        1301 => [1, 91, 101, 191],
        1401 => [1, 91, 101, 191, 201],
        1601 => [1, 201],
        _ => [1],
    };

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

    /// <summary>
    /// The skins (body IDs) players of a race can wear: body 1, and for Au Ra also body 101, the
    /// Xaela skin. No other race has a body 101.
    /// </summary>
    public static IReadOnlyList<ushort> SkinBodies(ushort genderRace)
        => genderRace is 1301 or 1401 ? [1, XaelaBody] : [1];

    /// <summary>The body ID of the Xaela skin.</summary>
    public const ushort XaelaBody = 101;

    /// <summary>Every playable race that wears <paramref name="skinGenderRace"/>'s skin, itself included.</summary>
    public static IReadOnlyList<ushort> SkinUsers(ushort skinGenderRace)
        => CustomizationKinds.Get(AssetKind.Body).AllowedGenderRaces
            .Where(race => GetSkinGenderRace(race) == skinGenderRace)
            .ToList();

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
