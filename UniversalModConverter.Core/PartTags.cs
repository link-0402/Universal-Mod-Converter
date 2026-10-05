using System.Text.RegularExpressions;

namespace UniversalModConverter.Core;

/// <summary>
/// Part variant tags: model attributes named <c>atr_{family}v_{a..j}</c>. The game shows a part
/// tagged <c>_a</c> to <c>_j</c> while bit 0 to 9 of the IMC attribute mask of the slot the model
/// is worn in is set, and only for that slot's own family (<see cref="GearSlots.PartTagFamily"/>):
/// a necklace never looks at <c>atr_tv_a</c>. On an accessory the game itself only checks
/// <c>_a</c>; Penumbra's custom shape and attribute support handles <c>_b</c> to <c>_j</c>.
/// </summary>
public static partial class PartTags
{
    [GeneratedRegex(@"^atr_(?<family>[a-z])v_(?<suffix>[a-j])\z", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    /// <summary>
    /// The new name of every tag in <paramref name="names"/> of <paramref name="from"/>'s family:
    /// <paramref name="to"/>'s family with the same suffix, so the same IMC bit still toggles it.
    /// Empty when both slots share a family or either has none. Every other name (tags of other
    /// families, <c>atrx_</c> customs, the game's own <c>atr_nek</c>) is left alone, as Penumbra's
    /// item swap does.
    /// </summary>
    public static Dictionary<string, string> Renames(IEnumerable<string> names, GearSlot from, GearSlot to)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (from.PartTagFamily() is not { } source || to.PartTagFamily() is not { } target || source == target)
            return renames;
        foreach (var name in names)
            if (TagRegex().Match(name) is { Success: true } match && match.Groups["family"].ValueSpan[0] == source)
                renames.TryAdd(name, $"atr_{target}v_{match.Groups["suffix"].Value}");
        return renames;
    }
}
