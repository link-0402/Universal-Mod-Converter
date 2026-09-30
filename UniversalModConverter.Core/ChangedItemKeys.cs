using System.Text.RegularExpressions;

namespace UniversalModConverter.Core;

/// <summary>
/// A hair, face, tail, ear or body a Penumbra collection changes, by the converter's own terms.
/// <paramref name="Id"/> 0 on <see cref="AssetKind.Body"/> is the race's skin textures, which
/// Penumbra lists without a body ID.
/// </summary>
public readonly record struct ChangedCustomization(AssetKind Kind, ushort GenderRace, ushort Id);

/// <summary>
/// Penumbra's names for what a collection changes (its "changed items"), as far as conversion
/// targets need them. Equipment is listed by item name alone. Hair, faces, tails, ears and
/// bodies are <c>Customization: {race} {gender} {slot} {id}</c>, with what the file is for in
/// parentheses when it is not the part itself: <c>Customization: Au Ra Female Face 1</c>,
/// <c>Customization: Au Ra Female Face (Iris) 1</c>, <c>Customization: Midlander Male Hair (Skeleton) 5</c>.
/// Skin textures are <c>Customization: Midlander Female Skin Textures</c>.
/// </summary>
public static partial class ChangedItemKeys
{
    /// <summary>Penumbra's race names, in gender/race code order: c01xx and c02xx are Midlanders, c07xx and c08xx Miqo'te.</summary>
    private static readonly string[] RaceNames =
        ["Midlander", "Highlander", "Elezen", "Miqo'te", "Roegadyn", "Lalafell", "Au Ra", "Hrothgar", "Viera"];

    // Children and NPC bodies ("Male (Child)") are not player options and do not match.
    [GeneratedRegex(@"^Customization: (?<race>.+) (?<gender>Male|Female) (?:(?<slot>Hair|Face|Tail|Ear|Body)(?: \([^)]*\))? (?<id>\d{1,4})|Skin Textures)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CustomizationRegex();

    public static bool TryParseCustomization(string key, out ChangedCustomization customization)
    {
        customization = default;
        var match = CustomizationRegex().Match(key);
        if (!match.Success) return false;
        var race = Array.IndexOf(RaceNames, match.Groups["race"].Value);
        if (race < 0) return false;

        var female = match.Groups["gender"].Value == "Female";
        var genderRace = (ushort)(((race * 2) + (female ? 2 : 1)) * 100 + 1);
        if (!match.Groups["slot"].Success)
        {
            customization = new ChangedCustomization(AssetKind.Body, genderRace, 0);
            return true;
        }

        var kind = match.Groups["slot"].Value switch
        {
            "Hair" => AssetKind.Hair,
            "Face" => AssetKind.Face,
            "Tail" => AssetKind.Tail,
            "Ear"  => AssetKind.VieraEar,
            _      => AssetKind.Body,
        };
        customization = new ChangedCustomization(kind, genderRace, ushort.Parse(match.Groups["id"].Value));
        return true;
    }
}
