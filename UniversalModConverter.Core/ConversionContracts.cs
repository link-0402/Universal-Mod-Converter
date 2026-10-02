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
/// The output modes a conversion cannot be written with, and why. The window offers a mode only
/// when these allow it and the planners refuse it when they do not, both from here, so what is
/// offered and what is refused cannot drift apart.
/// </summary>
public static class OutputModeRules
{
    /// <summary>A texture fan-out keeps its source and only adds paths.</summary>
    public const string FanOutInPlace = "A fan-out keeps its source; it adds to this mod or to a new one.";
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

    /// <summary>
    /// Whether a retarget's source race keeps its animation: adding to the mod always keeps it,
    /// converting in place moves it to the target races, and a new mod holds it when
    /// <paramref name="asked"/>.
    /// </summary>
    public static bool KeepsSourceRace(this ConversionOutputMode mode, bool asked) => mode switch
    {
        ConversionOutputMode.AddToMod => true,
        ConversionOutputMode.InPlace  => false,
        _                             => asked,
    };
}

/// <summary>What kinds of files a mod replaces under a root, for telling the user what a conversion touches.</summary>
[Flags]
public enum AssetContents
{
    None     = 0,
    Model    = 1,
    Material = 2,
    Texture  = 4,
    Other    = 8,
}

public static class AssetContentsExtensions
{
    /// <summary>The kind of file a game path is, by its extension.</summary>
    public static AssetContents Of(string gamePath) => Path.GetExtension(gamePath).ToLowerInvariant() switch
    {
        ".mdl"  => AssetContents.Model,
        ".mtrl" => AssetContents.Material,
        ".tex"  => AssetContents.Texture,
        _       => AssetContents.Other,
    };

    private static readonly AssetContents[] Tagged = [AssetContents.Model, AssetContents.Material, AssetContents.Texture];

    /// <summary>
    /// Every kind present that a conversion of the root works on, most significant first. Other
    /// files (metadata, VFX) are not what the user picks a root by, so they get no tag.
    /// </summary>
    public static IEnumerable<AssetContents> Tags(this AssetContents contents)
        => Tagged.Where(kind => contents.HasFlag(kind));

    /// <summary>"Model", "Material" or "Texture" for a single kind.</summary>
    public static string Name(this AssetContents kind) => kind switch
    {
        AssetContents.Model    => "Model",
        AssetContents.Material => "Material",
        AssetContents.Texture  => "Texture",
        _                      => "Other",
    };
}

public enum ConversionResultStatus
{
    NotStarted,
    Succeeded,
    PublishedButNotActivated,
    RolledBack,
    Failed,
}

public sealed record PlanDiagnostic(string Code, string Message, bool IsBlocker);

public sealed record ConversionOperation(
    string Category,
    string SourcePath,
    string TargetPath,
    bool Required = true);


