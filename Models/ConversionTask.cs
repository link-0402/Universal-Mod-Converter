using System.Collections.Generic;
using UniversalModConverter.Core;

namespace UniversalModConverter.Models;

/// <summary>Represents a single planned item conversion and all the changes it entails.</summary>
public class ConversionTask
{
    public AssetKind Kind { get; set; } = AssetKind.Gear;

    /// <summary>Whether the plan creates a new mod or edits the source mod in place.</summary>
    public ConversionOutputMode OutputMode { get; set; } = ConversionOutputMode.NewMod;

    /// <summary>The complete gear conversion plan; null for customization conversions.</summary>
    public GearConversionPlan? GearPlan { get; set; }

    /// <summary>Animations only: what to do, set before planning.</summary>
    public AnimationConversionRequest? AnimationRequest { get; set; }

    /// <summary>Animations only: the complete plan.</summary>
    public AnimationConversionPlan? AnimationPlan { get; set; }

    /// <summary>Target customization kind; null means the same kind as <see cref="Kind"/>.</summary>
    public AssetKind? TargetCustomizationKind { get; set; }

    public ushort? SourceGenderRace { get; set; }

    public ushort? TargetGenderRace { get; set; }

    public string SourceRoot { get; set; } = string.Empty;

    public string SourceFingerprint { get; set; } = string.Empty;

    public string PlanFingerprint { get; set; } = string.Empty;

    public List<PlanDiagnostic> Diagnostics { get; } = new();

    public ConversionResultStatus ResultStatus { get; set; } = ConversionResultStatus.NotStarted;

    public string? PublishedPath { get; set; }

    public string? RecoveryPath { get; set; }

    public string? JournalPath { get; set; }

    public bool HasBlockers => Diagnostics.Exists(d => d.IsBlocker);

    /// <summary>Absolute path to the root of the Penumbra mod folder.</summary>
    public string ModDirectory { get; set; } = string.Empty;

    /// <summary>The slot being converted.</summary>
    public EquipSlot Slot { get; set; } = EquipSlot.Body;

    /// <summary>Zero-padded source item ID string (e.g. "0164").</summary>
    public string OldIdPadded { get; set; } = string.Empty;

    /// <summary>Zero-padded target item ID string (e.g. "0200").</summary>
    public string NewIdPadded { get; set; } = string.Empty;

    /// <summary>When true, slot-specific filename filtering is skipped.</summary>
    public bool IgnoreSlot { get; set; } = false;

    /// <summary>
    /// For accessory cross-slot conversion: the output slot.
    /// When null (default), the output slot is identical to <see cref="Slot"/>.
    /// </summary>
    public EquipSlot? TargetSlot { get; set; } = null;

    /// <summary>
    /// Material variant index for the target item (1-based, matching the game's v000N
    /// material folder convention).  Used when creating a new mod to normalise all
    /// material-variant path components to this value and to inject IMC manipulations
    /// that redirect every game variant of the new item to this material variant.
    /// </summary>
    public int TargetVariant { get; set; } = 1;

    /// <summary>
    /// Material variant of the source item being converted (e.g. 4 for "9069-4").
    /// Used when multiple source material variants normalise to the same destination
    /// path so that the version matching the user-selected variant is preferred.
    /// 0 means unspecified (fall back to highest variant number).
    /// </summary>
    public int SourceVariant { get; set; } = 0;

    // ── Planned changes ───────────────────────────────────────────────────────

    public List<PlannedRename>      PlannedRenames      { get; } = new();
    public List<PlannedJsonChange>  PlannedJsonChanges  { get; } = new();
    public List<PlannedBinaryPatch> PlannedBinaryPatches{ get; } = new();
    public List<PlannedMdlChange>   PlannedMdlChanges   { get; } = new();
    public List<PlannedGeneratedFile> PlannedGeneratedFiles { get; } = new();

    /// <summary>
    /// All physical asset files discovered in the item's asset chain during planning
    /// (models, materials, textures – including files whose paths don't change).
    /// Populated by <see cref="ModConverterService.PlanConversion"/> and used when
    /// creating a new mod from just this item's assets.
    /// </summary>
    public List<string> AllAssetFiles { get; } = new();

    /// <summary>Gear only: the target models the output will ship, with their mesh groups.</summary>
    public List<GearOutputModel> OutputModels { get; } = new();

    /// <summary>
    /// Gear only: mesh groups the user removed, by output-model local path. Chosen after the
    /// preview and applied to the staged output, so changing it does not invalidate the plan.
    /// </summary>
    public Dictionary<string, MeshRemoval> MeshRemovals { get; } = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Output models whose body-material groups were already switched off by default, so that a
    /// group the user ticked back on stays on when the plan is previewed again.
    /// </summary>
    public HashSet<string> MeshDefaultsApplied { get; } = new(System.StringComparer.OrdinalIgnoreCase);

    // ── Extra targets ────────────────────────────────────────────────────────

    /// <summary>
    /// Customization textures only: further races or model IDs the same files are offered
    /// under, on top of <see cref="TargetGenderRace"/> and <see cref="NewIdPadded"/>. One
    /// texture can serve all of them because a texture holds no paths to retarget.
    /// </summary>
    public List<ConversionEndpoint> ExtraTargets { get; } = new();

    // ── Runs of several conversions ──────────────────────────────────────────

    /// <summary>
    /// The conversions of this run. Empty for a single conversion, which keeps using the
    /// scalar inputs above.
    /// </summary>
    public List<QueuedConversion> Entries { get; } = new();

    /// <summary>The plan the whole run produced; set only when <see cref="Entries"/> is used.</summary>
    public MergedModPlan? MergedPlan { get; set; }

    public bool IsQueue => Entries.Count > 0;

    /// <summary>
    /// A fresh task with the same inputs and nothing planned. A plan entry keeps its task as
    /// the record of what was chosen; each preview plans a copy, so the view never mistakes a
    /// re-planned task for the one it already drew.
    /// </summary>
    public ConversionTask CloneInputs()
    {
        var copy = new ConversionTask
        {
            Kind                    = Kind,
            OutputMode              = OutputMode,
            AnimationRequest        = AnimationRequest,
            TargetCustomizationKind = TargetCustomizationKind,
            SourceGenderRace        = SourceGenderRace,
            TargetGenderRace        = TargetGenderRace,
            ModDirectory            = ModDirectory,
            Slot                    = Slot,
            OldIdPadded             = OldIdPadded,
            NewIdPadded             = NewIdPadded,
            IgnoreSlot              = IgnoreSlot,
            TargetSlot              = TargetSlot,
            TargetVariant           = TargetVariant,
            SourceVariant           = SourceVariant,
        };
        copy.ExtraTargets.AddRange(ExtraTargets);
        return copy;
    }

    // ── State ────────────────────────────────────────────────────────────────

    public bool IsPlanned    { get; set; } = false;
    public bool IsApplied    { get; set; } = false;
    public string? ErrorMessage { get; set; }
}

/// <summary>A single file or directory rename.</summary>
public class PlannedRename
{
    public string OldPath   { get; set; } = string.Empty;
    public string NewPath   { get; set; } = string.Empty;
    public bool   IsDir     { get; set; } = false;
    public bool   Selected  { get; set; } = true;
}

/// <summary>A collection of field-level changes inside a single JSON file.</summary>
public class PlannedJsonChange
{
    public string FilePath   { get; set; } = string.Empty;
    public List<JsonFieldChange> Changes { get; } = new();
    public bool Selected { get; set; } = true;
}

/// <summary>One field replacement within a JSON file.</summary>
public class JsonFieldChange
{
    public string JsonPath  { get; set; } = string.Empty;
    public string OldValue  { get; set; } = string.Empty;
    public string NewValue  { get; set; } = string.Empty;
    public string ChangeType{ get; set; } = string.Empty; // "path_key", "path_value", "numeric_id"
    public bool   Selected  { get; set; } = true;
}

/// <summary>A binary resource with planned path replacements; MDL/MTRL string tables are rebuilt.</summary>
public class PlannedBinaryPatch
{
    public string FilePath   { get; set; } = string.Empty;
    public List<BinaryStringPatch> Patches { get; } = new();
    public bool Selected { get; set; } = true;

    /// <summary>True when patches were derived from complete resource-path strings.</summary>
    public bool IsStructured { get; set; }
}

/// <summary>A game dependency captured during preview for deterministic publication.</summary>
public sealed record PlannedGeneratedFile(string FilePath, string GamePath,
    System.Collections.Immutable.ImmutableArray<byte> Data);

/// <summary>A required, deterministic v6 MDL geometry rewrite.</summary>
public sealed class PlannedMdlChange
{
    public string FilePath { get; set; } = string.Empty;
    public ushort SourceGenderRace { get; set; }
    public ushort TargetGenderRace { get; set; }
    public string InputHash { get; set; } = string.Empty;
    public string OutputHash { get; set; } = string.Empty;
    public int Version { get; set; } = 6;
    public int LodCount { get; set; }
    public int MeshCount { get; set; }
    public int VertexCount { get; set; }
    public int ShapeVertexCount { get; set; }
    public bool GeometryConverted { get; set; }
    public RacialDeformationPlan? DeformationPlan { get; set; }
    public List<BoneResolution> BoneResolutions { get; } = new();
    public List<BinaryStringPatch> PathReplacements { get; } = new();
    public bool Selected { get; set; } = true;
}

/// <summary>One ASCII string replacement within a binary file.</summary>
public class BinaryStringPatch
{
    public string OldString { get; set; } = string.Empty;
    public string NewString { get; set; } = string.Empty;
    public bool   Selected  { get; set; } = true;
}

/// <summary>A remaining reference to the old item found after conversion.</summary>
public class LeftoverHit
{
    /// <summary>File or directory path where the leftover was found.</summary>
    public string FilePath  { get; set; } = string.Empty;

    /// <summary>"filename", "json", or "binary".</summary>
    public string HitType   { get; set; } = string.Empty;

    /// <summary>Human-readable description of what was found.</summary>
    public string Detail    { get; set; } = string.Empty;
}
