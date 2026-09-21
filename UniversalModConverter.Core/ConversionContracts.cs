namespace UniversalModConverter.Core;

public enum AssetKind
{
    Gear,
    Facewear,
    Hair,
    Face,
    Tail,
    VieraEar,

    /// <summary>The body under the gear: skin textures and the materials that load them.</summary>
    Body,
    Animation,
}

public enum ConversionOutputMode
{
    /// <summary>Build a separate mod and leave the source untouched.</summary>
    NewMod,

    /// <summary>Replace the source item in this mod with the converted one.</summary>
    InPlace,

    /// <summary>
    /// Add the converted item to this mod beside the original, in the same containers, so the
    /// option groups that already govern the original govern the new paths too.
    /// </summary>
    AddToMod,
}

/// <summary>
/// The questions the planners actually ask about an output mode. Comparing against a single
/// member is how a third mode silently takes the wrong branch, so ask these instead.
/// </summary>
public static class ConversionOutputModes
{
    public static bool IsNewMod(this ConversionOutputMode mode) => mode == ConversionOutputMode.NewMod;

    /// <summary>Writes into the source mod rather than building a separate one.</summary>
    public static bool EditsSourceMod(this ConversionOutputMode mode) => mode != ConversionOutputMode.NewMod;

    /// <summary>Leaves the source item working instead of moving it to the target.</summary>
    public static bool KeepsSource(this ConversionOutputMode mode) => mode == ConversionOutputMode.AddToMod;
}

public enum ConversionResultStatus
{
    NotStarted,
    Succeeded,
    PublishedButNotActivated,
    RolledBack,
    Failed,
}

public sealed record ConversionEndpoint(
    AssetKind Kind,
    ushort ModelId,
    ushort Variant = 0,
    string? Slot = null,
    ushort? GenderRace = null);

public sealed record PlanDiagnostic(string Code, string Message, bool IsBlocker);

public sealed record ConversionOperation(
    string Category,
    string SourcePath,
    string TargetPath,
    bool Required = true);


