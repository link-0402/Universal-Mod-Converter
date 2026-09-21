using System.Collections.Immutable;

namespace AdvancedPenumbraModConverter.Core;

/// <summary>Wearable gear slots, including the Glasses bonus slot used by facewear.</summary>
public enum GearSlot
{
    Head,
    Body,
    Hands,
    Legs,
    Feet,
    Ears,
    Neck,
    Wrists,
    RFinger,
    LFinger,
    Glasses,
}

/// <summary>A concrete gear item: slot, primary (set) ID and IMC variant.</summary>
public readonly record struct GearItem(GearSlot Slot, ushort SetId, ushort Variant)
{
    public bool IsAccessory => Slot.IsAccessory();
    public GearPathEndpoint PathEndpoint => new(IsAccessory, SetId, Slot.Suffix());
    public string Root => PathEndpoint.Root;
    public override string ToString() => $"{Slot} {PathEndpoint.Token}-{Variant}";
}

/// <summary>
/// Game and Penumbra naming for each gear slot. Glasses share the head model suffix,
/// head metadata (EQDP/IMC part) and equipment roots, but have their own human slot.
/// </summary>
public static class GearSlots
{
    /// <summary>Lower-case display name for messages, e.g. "legs", "right ring".</summary>
    public static string Label(this GearSlot slot) => slot switch
    {
        GearSlot.Ears    => "earring",
        GearSlot.RFinger => "right ring",
        GearSlot.LFinger => "left ring",
        GearSlot.Glasses => "facewear",
        _                => slot.ToString().ToLowerInvariant(),
    };

    /// <summary>Slots whose models are worn on the body and shaped to it: body, hands, legs and feet.</summary>
    public static bool IsBodyShaped(this GearSlot slot)
        => slot is GearSlot.Body or GearSlot.Hands or GearSlot.Legs or GearSlot.Feet;

    /// <summary>Slots whose models may carry body parts, pubes or piercings: the body-shaped ones and head gear.</summary>
    public static bool MayCarryBodyParts(this GearSlot slot) => slot.IsBodyShaped() || slot == GearSlot.Head;

    /// <summary>
    /// What changing slots means for the model, in at most two short sentences, or null when
    /// there is nothing worth saying. The model is not swapped for one of the target slot, which
    /// only matters where the target slot is itself shaped to the body; body parts, pubes and
    /// piercings are left out, which only matters where the source slot can have them.
    /// </summary>
    public static string? CrossSlotNote(GearSlot from, GearSlot to)
    {
        if (from == to) return null;
        var parts = new List<string>(2);
        if (to.IsBodyShaped()) parts.Add($"This will not turn the {from.Label()} model into a {to.Label()} model.");
        if (from.MayCarryBodyParts()) parts.Add("Body parts, pubes and piercings are left out automatically.");
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    public static bool IsAccessory(this GearSlot slot)
        => slot is GearSlot.Ears or GearSlot.Neck or GearSlot.Wrists or GearSlot.RFinger or GearSlot.LFinger;

    public static string Suffix(this GearSlot slot) => slot switch
    {
        GearSlot.Head or GearSlot.Glasses => "met",
        GearSlot.Body => "top",
        GearSlot.Hands => "glv",
        GearSlot.Legs => "dwn",
        GearSlot.Feet => "sho",
        GearSlot.Ears => "ear",
        GearSlot.Neck => "nek",
        GearSlot.Wrists => "wrs",
        GearSlot.RFinger => "rir",
        GearSlot.LFinger => "ril",
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    /// <summary>Penumbra's <c>EquipSlot</c> spelling used by Eqp, Eqdp and Imc manipulations.</summary>
    public static string EquipSlotName(this GearSlot slot) => slot switch
    {
        GearSlot.Glasses => "Head",
        _ => slot.ToString(),
    };

    /// <summary>Penumbra's <c>HumanSlot</c> spelling used by Shp and Atr manipulations.</summary>
    public static string HumanSlotName(this GearSlot slot) => slot.ToString();

    /// <summary>Shift of the two EQDP bits (material, model) for this slot.</summary>
    public static int EqdpShift(this GearSlot slot) => slot switch
    {
        GearSlot.Head or GearSlot.Glasses or GearSlot.Ears => 0,
        GearSlot.Body or GearSlot.Neck => 2,
        GearSlot.Hands or GearSlot.Wrists => 4,
        GearSlot.Legs or GearSlot.RFinger => 6,
        GearSlot.Feet or GearSlot.LFinger => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    public static int ImcPartIndex(this GearSlot slot) => slot.EqdpShift() / 2;

    public static string ImcObjectType(this GearSlot slot) => slot.IsAccessory() ? "Accessory" : "Equipment";

    /// <summary>Only real equipment (not glasses) uses the EQP table.</summary>
    public static bool HasEqp(this GearSlot slot) => !slot.IsAccessory() && slot != GearSlot.Glasses;

    /// <summary>Only the head slot uses the GMP (visor) table.</summary>
    public static bool HasGmp(this GearSlot slot) => slot == GearSlot.Head;

    /// <summary>EST (extra skeleton) type name, or null when the slot has none.</summary>
    public static string? EstType(this GearSlot slot) => slot switch
    {
        GearSlot.Head => "Head",
        GearSlot.Body => "Body",
        _ => null,
    };

    public static string? EstFile(this GearSlot slot) => slot switch
    {
        GearSlot.Head => "chara/xls/charadb/extra_met.est",
        GearSlot.Body => "chara/xls/charadb/extra_top.est",
        _ => null,
    };

    /// <summary>GlobalEqp type that keeps this accessory visible, or null.</summary>
    public static string? GlobalEqpType(this GearSlot slot) => slot switch
    {
        GearSlot.Ears => "DoNotHideEarrings",
        GearSlot.Neck => "DoNotHideNecklace",
        GearSlot.Wrists => "DoNotHideBracelets",
        GearSlot.RFinger => "DoNotHideRingR",
        GearSlot.LFinger => "DoNotHideRingL",
        _ => null,
    };

    public static string EqdpFile(this GearSlot slot, ushort genderRace)
        => $"chara/xls/charadb/{(slot.IsAccessory() ? "accessory" : "equipment")}deformerparameter/c{genderRace:D4}.eqdp";

    public static string ImcFile(GearItem item)
        => $"{item.Root}/{item.PathEndpoint.Token}.imc";

    public static string ModelPath(GearItem item, ushort genderRace)
        => $"{item.Root}/model/c{genderRace:D4}{item.PathEndpoint.Token}_{item.Slot.Suffix()}.mdl";

    public static string MaterialFolder(GearItem item, int materialId)
        => $"{item.Root}/material/v{materialId:D4}";

    public static string VfxPath(GearItem item, int vfxId)
        => $"{item.Root}/vfx/eff/ve{vfxId:D4}.avfx";
}

/// <summary>
/// Playable gender/race codes and the spellings Penumbra writes into mod JSON. For names to
/// show a user, see <see cref="RaceNames"/>; the two differ on purpose.
/// </summary>
public static class GenderRaces
{
    private static readonly string[] Races =
        ["Midlander", "Highlander", "Elezen", "Miqote", "Roegadyn", "Lalafell", "AuRa", "Hrothgar", "Viera"];

    public static ImmutableArray<ushort> Playable { get; } =
        Enumerable.Range(1, 18).Select(i => (ushort)(i * 100 + 1)).ToImmutableArray();

    public static (string Race, string Gender) Names(ushort code)
    {
        var index = code / 100;
        if (code % 100 != 1 || index is < 1 or > 18)
            throw new ArgumentOutOfRangeException(nameof(code), code, "Not a playable gender/race code.");
        return (Races[(index - 1) / 2], index % 2 == 1 ? "Male" : "Female");
    }

    public static bool TryParse(string? race, string? gender, out ushort code)
    {
        code = 0;
        var raceIndex = Array.FindIndex(Races, r => r.Equals(race, StringComparison.OrdinalIgnoreCase));
        if (raceIndex < 0) return false;
        var offset = "Male".Equals(gender, StringComparison.OrdinalIgnoreCase) ? 1
            : "Female".Equals(gender, StringComparison.OrdinalIgnoreCase) ? 2 : 0;
        if (offset == 0) return false;
        code = (ushort)((raceIndex * 2 + offset) * 100 + 1);
        return true;
    }
}
