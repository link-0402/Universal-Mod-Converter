namespace UniversalModConverter.Core;

/// <summary>
/// Turns a gender/race code into the name the game uses for it, so plan messages can say
/// "Midlander Female" instead of "c0201".
/// <para>
/// These are display names and deliberately differ from <see cref="GenderRaces"/>, which
/// holds the spellings Penumbra writes into mod JSON (<c>Miqote</c>, <c>AuRa</c>). Never use
/// one where the other belongs: a display name in JSON would not round-trip.
/// </para>
/// </summary>
public static class RaceNames
{
    private static readonly string[] Races =
    [
        "Midlander", "Highlander", "Elezen", "Miqo'te", "Roegadyn",
        "Lalafell", "Au Ra", "Hrothgar", "Viera",
    ];

    /// <summary>"Midlander Female", or the raw code when it is not a playable race.</summary>
    public static string Name(ushort genderRace)
    {
        var index = genderRace / 100 - 1;
        if (genderRace % 100 != 1 || index < 0 || index >= Races.Length * 2) return Code(genderRace);
        return $"{Races[index / 2]} {(index % 2 == 0 ? "Male" : "Female")}";
    }

    /// <summary>
    /// "Midlander Female (c0201)": the name for the reader, the code for anyone comparing the
    /// message against file paths. Collapses to the bare code when there is no name to add.
    /// </summary>
    public static string Describe(ushort genderRace)
    {
        var name = Name(genderRace);
        var code = Code(genderRace);
        return name == code ? code : $"{name} ({code})";
    }

    public static string Code(ushort genderRace) => $"c{genderRace:D4}";
}
