using UniversalModConverter.Core;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using Character = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace UniversalModConverter.Services;

/// <summary>
/// What the local player visibly wears. Read from the drawn model, which is what Glamourer and
/// every other appearance change act on; the character's own gear data can lag behind or keep
/// the real gear while a glamour is shown.
/// </summary>
public sealed unsafe class WornGearService(IObjectTable objects)
{
    /// <summary>
    /// Whether the local player wears a model of <paramref name="item"/>'s set in its slot; the
    /// variant does not matter, because every variant uses the same model. Null when there is no
    /// player to look at.
    /// </summary>
    public bool? Wears(GearItem item)
        => Worn(item.Slot) is { } worn ? worn.Id == item.SetId : null;

    /// <summary>The model set shown in <paramref name="slot"/>, or null when there is no player to look at.</summary>
    public ushort? WornSet(GearSlot slot) => Worn(slot)?.Id;

    /// <summary>The two dyes the local player shows in <paramref name="slot"/>, or none when unknown.</summary>
    public byte[] Stains(GearSlot slot)
        => Worn(slot) is { } worn ? [worn.Stain0, worn.Stain1] : [0, 0];

    private EquipmentModelId? Worn(GearSlot slot)
    {
        if (objects.LocalPlayer is not { } player || player.Address == 0) return null;
        var character = (Character*)player.Address;

        var drawObject = (CharacterBase*)character->DrawObject;
        if (drawObject != null && drawObject->GetModelType() == CharacterBase.ModelType.Human)
        {
            var human = (Human*)drawObject;
            return slot switch
            {
                GearSlot.Head    => human->Head,
                GearSlot.Body    => human->Top,
                GearSlot.Hands   => human->Arms,
                GearSlot.Legs    => human->Legs,
                GearSlot.Feet    => human->Feet,
                GearSlot.Ears    => human->Ear,
                GearSlot.Neck    => human->Neck,
                GearSlot.Wrists  => human->Wrist,
                GearSlot.RFinger => human->RFinger,
                GearSlot.LFinger => human->LFinger,
                GearSlot.Glasses => human->Glasses0,
                _                => null,
            };
        }

        // Not drawn right now (e.g. mid-redraw): fall back to the gear the character carries.
        if (slot == GearSlot.Glasses) return null;
        return character->DrawData.Equipment((DrawDataContainer.EquipmentSlot)(int)slot);
    }
}
