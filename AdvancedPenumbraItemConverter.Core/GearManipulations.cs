using System.Text.Json.Nodes;

namespace AdvancedPenumbraItemConverter.Core;

public enum RetargetKind
{
    /// <summary>Not about the source item (another set, slot, or a global manipulation).</summary>
    Unrelated,

    /// <summary>About the source item but a different IMC variant than the converted one.</summary>
    OtherVariant,

    /// <summary>About the source item but meaningless for the target slot.</summary>
    NotTransferable,

    Retargeted,
}

public sealed record RetargetOutcome(RetargetKind Kind, IReadOnlyList<JsonObject> Results, string Reason = "")
{
    public static RetargetOutcome Unrelated { get; } = new(RetargetKind.Unrelated, []);
    public static RetargetOutcome OtherVariant { get; } = new(RetargetKind.OtherVariant, []);
}

/// <summary>
/// Identifies and retargets Penumbra meta manipulations (<c>{"Type": ..., "Manipulation": {...}}</c>)
/// that belong to a gear item. Field names follow Penumbra's schemas.
/// </summary>
public static class GearManipulations
{
    public static RetargetOutcome Retarget(JsonObject manipulation, GearItem source, GearItem target,
        ushort sourceVariant, IReadOnlyList<ushort> targetVariants)
    {
        if (manipulation["Manipulation"] is not JsonObject m) return RetargetOutcome.Unrelated;
        var type = Json.GetString(manipulation["Type"]) ?? string.Empty;
        switch (type.ToLowerInvariant())
        {
            case "eqp":
                if (!source.Slot.HasEqp() || !IsSet(m["SetId"], source) || !Json.StringEquals(m["Slot"], source.Slot.EquipSlotName()))
                    return RetargetOutcome.Unrelated;
                if (!target.Slot.HasEqp() || target.Slot != source.Slot)
                    return NotTransferable(manipulation, $"visibility flags for {source.Slot} cannot apply to {target.Slot}");
                return Single(manipulation, c => c["SetId"] = Json.SameKindNumber(c["SetId"], target.SetId));

            case "eqdp":
                if (!IsEqdpFor(manipulation, source, null)) return RetargetOutcome.Unrelated;
                return Single(manipulation, c =>
                {
                    c["SetId"] = Json.SameKindNumber(c["SetId"], target.SetId);
                    c["Slot"] = target.Slot.EquipSlotName();
                    if (Json.TryGetInt(c["Entry"], out var entry))
                        c["Entry"] = Json.SameKindNumber(c["Entry"],
                            GameMetadata.RepositionEqdp((ushort)entry, source.Slot, target.Slot));
                });

            case "imc":
                if (!IsImcFor(manipulation, source, null)) return RetargetOutcome.Unrelated;
                if (Json.GetInt(m["Variant"], -1) != sourceVariant) return RetargetOutcome.OtherVariant;
                return new RetargetOutcome(RetargetKind.Retargeted, targetVariants.Select(variant =>
                {
                    var clone = (JsonObject)manipulation.DeepClone();
                    var c = (JsonObject)clone["Manipulation"]!;
                    c["PrimaryId"] = Json.SameKindNumber(c["PrimaryId"], target.SetId);
                    c["Variant"] = Json.SameKindNumber(c["Variant"], variant);
                    c["ObjectType"] = target.Slot.ImcObjectType();
                    c["EquipSlot"] = target.Slot.EquipSlotName();
                    return clone;
                }).ToArray());

            case "est":
                if (source.Slot.EstType() is not { } estType || !IsSet(m["SetId"], source) ||
                    !Json.StringEquals(m["Slot"], estType))
                    return RetargetOutcome.Unrelated;
                if (target.Slot.EstType() != estType)
                    return NotTransferable(manipulation, $"extra skeletons for {source.Slot} cannot apply to {target.Slot}");
                return Single(manipulation, c => c["SetId"] = Json.SameKindNumber(c["SetId"], target.SetId));

            case "gmp":
                if (!source.Slot.HasGmp() || !IsSet(m["SetId"], source)) return RetargetOutcome.Unrelated;
                if (!target.Slot.HasGmp())
                    return NotTransferable(manipulation, $"visor settings cannot apply to {target.Slot}");
                return Single(manipulation, c => c["SetId"] = Json.SameKindNumber(c["SetId"], target.SetId));

            case "shp":
            case "atr":
                // Without an Id the entry applies to every item in the slot.
                if (!Json.StringEquals(m["Slot"], source.Slot.HumanSlotName()) || m["Id"] == null || !IsSet(m["Id"], source))
                    return RetargetOutcome.Unrelated;
                return Single(manipulation, c =>
                {
                    c["Slot"] = target.Slot.HumanSlotName();
                    c["Id"] = Json.SameKindNumber(c["Id"], target.SetId);
                });

            case "globaleqp":
                if (source.Slot.GlobalEqpType() is not { } globalType || !Json.StringEquals(m["Type"], globalType) ||
                    !IsSet(m["Condition"], source))
                    return RetargetOutcome.Unrelated;
                if (target.Slot.GlobalEqpType() is not { } targetType)
                    return NotTransferable(manipulation, $"'{globalType}' has no equivalent for {target.Slot}");
                return Single(manipulation, c =>
                {
                    c["Type"] = targetType;
                    c["Condition"] = Json.SameKindNumber(c["Condition"], target.SetId);
                });

            default:
                return RetargetOutcome.Unrelated;
        }
    }

    public static bool IsEqdpFor(JsonObject manipulation, GearItem item, ushort? genderRace)
    {
        if (!Json.StringEquals(manipulation["Type"], "Eqdp") || manipulation["Manipulation"] is not JsonObject m) return false;
        if (!IsSet(m["SetId"], item) || !Json.StringEquals(m["Slot"], item.Slot.EquipSlotName())) return false;
        if (genderRace is not { } code) return true;
        var (race, gender) = GenderRaces.Names(code);
        return Json.StringEquals(m["Race"], race) && Json.StringEquals(m["Gender"], gender);
    }

    public static bool IsImcFor(JsonObject manipulation, GearItem item, ushort? variant)
        => Json.StringEquals(manipulation["Type"], "Imc") &&
           manipulation["Manipulation"] is JsonObject m &&
           ImcIdentifierMatches(m, item) &&
           (variant == null || Json.GetInt(m["Variant"], -1) == variant);

    /// <summary>True when an IMC group applies to the item's given variant.</summary>
    public static bool ImcGroupMatches(JsonObject group, GearItem item, ushort variant)
    {
        if (group["Identifier"] is not JsonObject identifier || !ImcIdentifierMatches(identifier, item)) return false;
        return Json.GetInt(identifier["Variant"], -1) == variant ||
               (Json.TryGetBool(group["AllVariants"], out var all) && all);
    }

    private static bool ImcIdentifierMatches(JsonObject identifier, GearItem item)
        => Json.StringEquals(identifier["ObjectType"], item.Slot.ImcObjectType()) &&
           IsSet(identifier["PrimaryId"], item) &&
           Json.StringEquals(identifier["EquipSlot"], item.Slot.EquipSlotName());

    private static bool IsSet(JsonNode? node, GearItem item) => Json.TryGetInt(node, out var id) && id == item.SetId;

    private static RetargetOutcome Single(JsonObject manipulation, Action<JsonObject> edit)
    {
        var clone = (JsonObject)manipulation.DeepClone();
        edit((JsonObject)clone["Manipulation"]!);
        return new RetargetOutcome(RetargetKind.Retargeted, [clone]);
    }

    private static RetargetOutcome NotTransferable(JsonObject manipulation, string reason)
        => new(RetargetKind.NotTransferable, [], $"{Describe(manipulation)}: {reason}.");

    // ── Construction ────────────────────────────────────────────────────────

    public static JsonObject Eqdp(GearItem item, ushort genderRace, ushort entry)
    {
        var (race, gender) = GenderRaces.Names(genderRace);
        return Wrap("Eqdp", new JsonObject
        {
            ["Entry"] = entry,
            ["Gender"] = gender,
            ["Race"] = race,
            ["SetId"] = item.SetId,
            ["Slot"] = item.Slot.EquipSlotName(),
        });
    }

    public static JsonObject Eqp(GearItem item, ulong entry)
        => Wrap("Eqp", new JsonObject { ["Entry"] = entry, ["SetId"] = item.SetId, ["Slot"] = item.Slot.EquipSlotName() });

    public static JsonObject Gmp(GearItem item, ulong entry)
        => Wrap("Gmp", new JsonObject { ["Entry"] = GameMetadata.GmpToJson(entry), ["SetId"] = item.SetId });

    public static JsonObject Est(GearItem item, ushort genderRace, ushort skeleton)
    {
        var (race, gender) = GenderRaces.Names(genderRace);
        return Wrap("Est", new JsonObject
        {
            ["Entry"] = skeleton,
            ["Gender"] = gender,
            ["Race"] = race,
            ["SetId"] = item.SetId,
            ["Slot"] = item.Slot.EstType(),
        });
    }

    public static JsonObject Imc(GearItem item, ushort variant, ImcEntry entry)
        => Wrap("Imc", new JsonObject
        {
            ["Entry"] = entry.ToJson(),
            ["PrimaryId"] = item.SetId,
            ["SecondaryId"] = 0,
            ["Variant"] = variant,
            ["ObjectType"] = item.Slot.ImcObjectType(),
            ["EquipSlot"] = item.Slot.EquipSlotName(),
            ["BodySlot"] = "Unknown",
        });

    private static JsonObject Wrap(string type, JsonObject manipulation)
        => new() { ["Type"] = type, ["Manipulation"] = manipulation };

    // ── Identity / display ──────────────────────────────────────────────────

    /// <summary>
    /// The identity Penumbra deduplicates manipulations by: the type plus every identifier
    /// field (everything except the entry).
    /// </summary>
    public static string Identity(JsonObject manipulation)
    {
        var type = (Json.GetString(manipulation["Type"]) ?? string.Empty).ToLowerInvariant();
        if (manipulation["Manipulation"] is not JsonObject m) return type;
        var parts = m.Where(p => p.Key != "Entry")
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Key.ToLowerInvariant() + "=" + Canonical(p.Value));
        return type + "|" + string.Join("|", parts);

        static string Canonical(JsonNode? value)
            => Json.TryGetInt(value, out var number) ? number.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : (Json.GetString(value) ?? value?.ToJsonString() ?? "null").ToLowerInvariant();
    }

    public static string Describe(JsonObject manipulation)
    {
        var type = Json.GetString(manipulation["Type"]) ?? "?";
        if (manipulation["Manipulation"] is not JsonObject m) return type;
        var fields = new[] { "Type", "Slot", "EquipSlot", "SetId", "PrimaryId", "Id", "Variant", "Race", "Gender", "Condition" }
            .Where(m.ContainsKey)
            .Select(k => $"{k}={(Json.GetString(m[k]) ?? m[k]?.ToJsonString())}");
        var entry = m["Entry"]?.ToJsonString() ?? string.Empty;
        return $"{type}[{string.Join(", ", fields)}] {(entry.Length > 60 ? entry[..60] + "…" : entry)}";
    }
}
