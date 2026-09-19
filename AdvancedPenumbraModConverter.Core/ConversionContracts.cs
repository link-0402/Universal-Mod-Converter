using System.Collections.Immutable;

namespace AdvancedPenumbraModConverter.Core;

public enum AssetKind
{
    Gear,
    Facewear,
    Hair,
    Face,
    Tail,
    VieraEar,
    Animation,
}

public enum ConversionOutputMode
{
    NewMod,
    InPlace,
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

public sealed record ConversionRequest(
    string ModDirectory,
    string SourceRoot,
    ConversionEndpoint Source,
    ConversionEndpoint Target,
    ConversionOutputMode OutputMode,
    string? OutputDirectory = null,
    string? DisplayName = null);

public sealed record PlanDiagnostic(string Code, string Message, bool IsBlocker);

public sealed record ConversionOperation(
    string Category,
    string SourcePath,
    string TargetPath,
    bool Required = true);

public sealed record ConversionPlan(
    ConversionRequest Request,
    string SourceFingerprint,
    string PlanFingerprint,
    ImmutableArray<ConversionOperation> Operations,
    ImmutableArray<PlanDiagnostic> Diagnostics)
{
    public bool CanApply => Diagnostics.All(d => !d.IsBlocker);
}

public sealed record ConversionResult(
    ConversionResultStatus Status,
    string? PublishedPath,
    string? RecoveryPath,
    ImmutableArray<string> Warnings,
    string? Error)
{
    public bool IsSuccess => Status is ConversionResultStatus.Succeeded;

    public static ConversionResult Failed(string error)
        => new(ConversionResultStatus.Failed, null, null, [], error);
}
