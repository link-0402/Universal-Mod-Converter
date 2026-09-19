using System;
using System.Collections.Generic;
using AdvancedPenumbraItemConverter.Core;

namespace AdvancedPenumbraItemConverter.Models;

/// <summary>Identifies an equipment slot using Penumbra/FFXIV naming conventions.</summary>
public enum EquipSlot
{
    Head,
    Body,
    Hands,
    Legs,
    Feet,
    Earring,
    Neck,
    Wrists,
    RingRight,
    RingLeft,
    Facewear,
}

/// <summary>Maps between slot enums and the abbreviated game keys used in filenames.</summary>
public static class SlotInfo
{
    public static readonly IReadOnlyDictionary<EquipSlot, string> KeyMap =
        new Dictionary<EquipSlot, string>
        {
            { EquipSlot.Head,      "met" },
            { EquipSlot.Body,      "top" },
            { EquipSlot.Hands,     "glv" },
            { EquipSlot.Legs,      "dwn" },
            { EquipSlot.Feet,      "sho" },
            { EquipSlot.Earring,   "ear" },
            { EquipSlot.Neck,      "nek" },
            { EquipSlot.Wrists,    "wrs" },
            { EquipSlot.RingRight, "rir" },
            { EquipSlot.RingLeft,  "ril" },
            // Facewear is a distinct logical category but uses the head model suffix.
            { EquipSlot.Facewear,  "met" },
        };

    public static readonly IReadOnlyDictionary<string, EquipSlot> ReverseMap;

    /// <summary>
    /// Maps slots to the EquipSlot string values used in Penumbra mod JSON
    /// (manipulation "Slot"/"EquipSlot" fields).  These must match Penumbra's
    /// EquipSlot enum spellings exactly, e.g. "Ears", "RFinger", "LFinger".
    /// </summary>
    public static readonly IReadOnlyDictionary<EquipSlot, string> LabelMap =
        new Dictionary<EquipSlot, string>
        {
            { EquipSlot.Head,      "Head"    },
            { EquipSlot.Body,      "Body"    },
            { EquipSlot.Hands,     "Hands"   },
            { EquipSlot.Legs,      "Legs"    },
            { EquipSlot.Feet,      "Feet"    },
            { EquipSlot.Earring,   "Ears"    },
            { EquipSlot.Neck,      "Neck"    },
            { EquipSlot.Wrists,    "Wrists"  },
            { EquipSlot.RingRight, "RFinger" },
            { EquipSlot.RingLeft,  "LFinger" },
            { EquipSlot.Facewear,  "Head"    },
        };

    /// <summary>Human-readable slot names for UI display.</summary>
    public static readonly IReadOnlyDictionary<EquipSlot, string> DisplayLabelMap =
        new Dictionary<EquipSlot, string>
        {
            { EquipSlot.Head,      "Head"       },
            { EquipSlot.Body,      "Body"       },
            { EquipSlot.Hands,     "Hands"      },
            { EquipSlot.Legs,      "Legs"       },
            { EquipSlot.Feet,      "Feet"       },
            { EquipSlot.Earring,   "Earring"    },
            { EquipSlot.Neck,      "Neck"       },
            { EquipSlot.Wrists,    "Wrists"     },
            { EquipSlot.RingRight, "Ring Right" },
            { EquipSlot.RingLeft,  "Ring Left"  },
            { EquipSlot.Facewear,  "Facewear"   },
        };

    private static readonly HashSet<EquipSlot> _accessories = new()
    {
        EquipSlot.Earring,
        EquipSlot.Neck,
        EquipSlot.Wrists,
        EquipSlot.RingRight,
        EquipSlot.RingLeft,
    };

    static SlotInfo()
    {
        var rev = new Dictionary<string, EquipSlot>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, key) in KeyMap)
            // Facewear shares the physical "met" suffix with Head; discovery starts
            // from Head and expands the logical ambiguity using the game catalogs.
            rev.TryAdd(key, slot);
        ReverseMap = rev;
    }

    /// <summary>Returns "a" for accessories, "e" for equipment.</summary>
    public static string ItemPrefix(EquipSlot slot) => _accessories.Contains(slot) ? "a" : "e";

    public static bool IsAccessory(EquipSlot slot) => _accessories.Contains(slot);

    public static bool IsFacewear(EquipSlot slot) => slot == EquipSlot.Facewear;

    public static List<EquipSlot> AllSlots { get; } = new List<EquipSlot>(KeyMap.Keys);

    /// <summary>Maps the UI slot to the conversion engine's slot (facewear is the Glasses slot).</summary>
    public static GearSlot ToGearSlot(EquipSlot slot) => slot switch
    {
        EquipSlot.Head      => GearSlot.Head,
        EquipSlot.Body      => GearSlot.Body,
        EquipSlot.Hands     => GearSlot.Hands,
        EquipSlot.Legs      => GearSlot.Legs,
        EquipSlot.Feet      => GearSlot.Feet,
        EquipSlot.Earring   => GearSlot.Ears,
        EquipSlot.Neck      => GearSlot.Neck,
        EquipSlot.Wrists    => GearSlot.Wrists,
        EquipSlot.RingRight => GearSlot.RFinger,
        EquipSlot.RingLeft  => GearSlot.LFinger,
        EquipSlot.Facewear  => GearSlot.Glasses,
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, null),
    };
}
