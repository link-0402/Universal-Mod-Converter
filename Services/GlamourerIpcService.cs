using System;
using System.Collections.Generic;
using System.Linq;
using AdvancedPenumbraModConverter.Core;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;

namespace AdvancedPenumbraModConverter.Services;

/// <summary>
/// The one Glamourer call the converter needs: putting an item on the local player, so the
/// Mesh groups preview has the source item to work on. The change is temporary (Glamourer's
/// "once" flag): it lasts until the player changes gear or Glamourer reapplies a design.
/// </summary>
public sealed class GlamourerIpcService(IDalamudPluginInterface pi, IPluginLog log)
{
    /// <summary>Glamourer's <c>ApplyFlag.Once | ApplyFlag.Equipment</c>.</summary>
    private const ulong ApplyOnceEquipment = 0x01 | 0x02;

    private readonly ICallGateSubscriber<int, byte, ulong, IReadOnlyList<byte>, uint, ulong, int> _setItem =
        pi.GetIpcSubscriber<int, byte, ulong, IReadOnlyList<byte>, uint, ulong, int>("Glamourer.SetItem.V3");

    private readonly ICallGateSubscriber<int, byte, ulong, uint, ulong, int> _setBonusItem =
        pi.GetIpcSubscriber<int, byte, ulong, uint, ulong, int>("Glamourer.SetBonusItem");

    public bool IsAvailable
        => pi.InstalledPlugins.Any(p => p.IsLoaded &&
                                        string.Equals(p.InternalName, "Glamourer", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Puts <paramref name="itemId"/> into <paramref name="slot"/> of the local player with the
    /// given dyes; returns whether Glamourer accepted it. The dyes go over as a <see cref="List{T}"/>:
    /// a byte array reaches Glamourer as null (Dalamud's IPC converts it as a base64 string, which
    /// does not turn back into a list), and Glamourer throws on it.
    /// </summary>
    public bool EquipOnPlayer(GearSlot slot, ulong itemId, IReadOnlyList<byte> stains)
    {
        try
        {
            var rc = slot == GearSlot.Glasses
                ? _setBonusItem.InvokeFunc(0, 1, itemId, 0, ApplyOnceEquipment)
                : _setItem.InvokeFunc(0, ApiSlot(slot), itemId, new List<byte>(stains), 0, ApplyOnceEquipment);
            if (rc != 0) log.Warning($"[APMC] Glamourer SetItem returned {rc} for item {itemId} ({slot}).");
            return rc == 0;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "[APMC] Glamourer SetItem failed");
            return false;
        }
    }

    /// <summary>Glamourer's <c>ApiEquipSlot</c> numbering.</summary>
    private static byte ApiSlot(GearSlot slot) => slot switch
    {
        GearSlot.Head    => 3,
        GearSlot.Body    => 4,
        GearSlot.Hands   => 5,
        GearSlot.Legs    => 7,
        GearSlot.Feet    => 8,
        GearSlot.Ears    => 9,
        GearSlot.Neck    => 10,
        GearSlot.Wrists  => 11,
        GearSlot.RFinger => 12,
        GearSlot.LFinger => 14,
        _                => 0,
    };
}
