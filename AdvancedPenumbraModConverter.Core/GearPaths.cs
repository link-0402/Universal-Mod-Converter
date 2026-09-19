using System.Text.RegularExpressions;

namespace AdvancedPenumbraModConverter.Core;

/// <summary>A TexTools-style equipment/accessory dependency-root endpoint.</summary>
public readonly record struct GearPathEndpoint(bool IsAccessory, ushort SetId, string Slot)
{
    public char Prefix => IsAccessory ? 'a' : 'e';
    public string Category => IsAccessory ? "accessory" : "equipment";
    public string Token => $"{Prefix}{SetId:D4}";
    public string Root => $"chara/{Category}/{Token}";
}

/// <summary>
/// Retargets equipment/accessory paths as one root-aware operation.  This mirrors
/// TexTools RootCloner.UpdatePath: the dependency root, item token, and slot/part
/// suffix move together instead of being independently replaced throughout a string.
/// </summary>
public static partial class GearPaths
{
    [GeneratedRegex(@"(?<![A-Za-z0-9])chara(?<s1>[/\\])(?<category>equipment|accessory)(?<s2>[/\\])(?<token>[ea]\d{4})(?=[/\\])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RootRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])chara(?<s1>[/\\])common(?<s2>[/\\])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommonRootRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<race>c\d{4})(?<token>[ea]\d{4})(?<slot>_[a-z]{3})?(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RacialFileTokenRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<token>[ea]\d{4})(?<slot>_[a-z]{3})?(?![A-Za-z0-9/\\])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ItemTokenRegex();

    public static bool IsRootPath(string value, GearPathEndpoint endpoint)
        => RootRegex().Matches(value).Any(match => IsSourceRoot(match, endpoint));

    /// <summary>
    /// Rewrites a complete game path. Dependencies below chara/common are moved
    /// into the destination root's common folder just as TexTools does.
    /// </summary>
    public static string RewriteGamePath(string value, GearPathEndpoint source,
        GearPathEndpoint target, bool relocateCommon = false)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var rewritten = value;
        if (relocateCommon)
            rewritten = CommonRootRegex().Replace(rewritten, match =>
            {
                var separator = match.Groups["s1"].Value;
                return $"chara{separator}{target.Category}{separator}{target.Token}{separator}" +
                       $"common{match.Groups["s2"].Value}";
            });

        rewritten = RootRegex().Replace(rewritten, match =>
        {
            if (!IsSourceRoot(match, source)) return match.Value;
            var s1 = match.Groups["s1"].Value;
            var s2 = match.Groups["s2"].Value;
            return $"chara{s1}{target.Category}{s2}{target.Token}";
        });
        return RewriteOwnedReference(rewritten, source, target);
    }

    /// <summary>
    /// Rewrites a reference already proven to belong to the source root. This also
    /// handles the short material names stored in MDL string tables.
    /// </summary>
    public static string RewriteOwnedReference(string value, GearPathEndpoint source,
        GearPathEndpoint target)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var rewritten = RacialFileTokenRegex().Replace(value, match =>
        {
            if (!match.Groups["token"].Value.Equals(source.Token, StringComparison.OrdinalIgnoreCase))
                return match.Value;
            return match.Groups["race"].Value + target.Token +
                   RetargetSlot(match.Groups["slot"].Value, source.Slot, target.Slot);
        });

        // Root folders, IMC-like names, and user-created local filenames can carry
        // only eNNNN/aNNNN. Racial chunks were consumed above and are excluded by
        // the token regex's alphanumeric boundary.
        return ItemTokenRegex().Replace(rewritten, match =>
        {
            if (!match.Groups["token"].Value.Equals(source.Token, StringComparison.OrdinalIgnoreCase))
                return match.Value;
            return target.Token + RetargetSlot(match.Groups["slot"].Value, source.Slot, target.Slot);
        });
    }

    private static string RetargetSlot(string suffix, string sourceSlot, string targetSlot)
    {
        if (string.IsNullOrEmpty(targetSlot)) return suffix;
        if (string.IsNullOrEmpty(suffix)) return suffix;
        var sourceSuffix = "_" + sourceSlot;
        if (suffix.Equals(sourceSuffix, StringComparison.OrdinalIgnoreCase))
            return "_" + targetSlot;

        // TexTools preserves deliberately cross-referenced/fake part tags and adds
        // the destination slot to keep the cloned filename collision-free.
        var targetSuffix = "_" + targetSlot;
        return suffix.EndsWith(targetSuffix, StringComparison.OrdinalIgnoreCase)
            ? suffix
            : suffix + targetSuffix;
    }

    private static bool IsSourceRoot(Match match, GearPathEndpoint source)
        => match.Groups["category"].Value.Equals(source.Category, StringComparison.OrdinalIgnoreCase) &&
           match.Groups["token"].Value.Equals(source.Token, StringComparison.OrdinalIgnoreCase);

}
