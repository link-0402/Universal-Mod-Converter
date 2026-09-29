namespace UniversalModConverter.Core;

/// <summary>A plan that is written as file operations plus a complete mod definition.</summary>
public interface IModFilePlan
{
    PenumbraMod Result { get; }

    IReadOnlyList<PlannedFileOperation> Files { get; }

    /// <summary>What planning found; any blocker keeps the plan from being written.</summary>
    IReadOnlyList<PlanDiagnostic> Diagnostics { get; }

    /// <summary>Absolute paths of the mod's files the plan was derived from; a change to any of them outdates it.</summary>
    IReadOnlySet<string> InputFiles { get; }

    /// <summary>The output mode the plan was made for.</summary>
    ConversionOutputMode Mode { get; }

    bool HasBlockers { get; }

    /// <summary>Deterministic hash of the planned output, used to prove Apply matches Preview.</summary>
    string Fingerprint();

    /// <summary>
    /// Lets go of the file contents once the plan has been written. An applied plan stays on
    /// screen as the record of what was done, and what it wrote (retargeted animations, rebuilt
    /// models) can run to hundreds of megabytes.
    /// </summary>
    void ReleaseContents();
}

/// <summary>What the gear, animation and texture planners produce alike.</summary>
public abstract class ModFilePlan(PenumbraMod result) : IModFilePlan
{
    /// <summary>The complete definition of the output mod (in place: the edited mod).</summary>
    public PenumbraMod Result { get; } = result;

    public List<PlannedFileOperation> Files { get; } = [];

    IReadOnlyList<PlannedFileOperation> IModFilePlan.Files => Files;

    /// <summary>The lines of the preview.</summary>
    public List<GearPlanChange> Changes { get; } = [];

    public List<PlanDiagnostic> Diagnostics { get; } = [];

    IReadOnlyList<PlanDiagnostic> IModFilePlan.Diagnostics => Diagnostics;

    public HashSet<string> InputFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    IReadOnlySet<string> IModFilePlan.InputFiles => InputFiles;

    public abstract ConversionOutputMode Mode { get; }

    public bool HasBlockers => Diagnostics.Any(d => d.IsBlocker);

    /// <summary>Records a finding once, however often planning comes across it (once per target, say).</summary>
    internal void Report(string code, string message, bool blocker)
    {
        if (!Diagnostics.Any(d => d.Code == code && d.Message == message))
            Diagnostics.Add(new PlanDiagnostic(code, message, blocker));
    }

    /// <summary>
    /// Covers the whole result definition, so in a run of several conversions every entry hashes
    /// the same shared definition; fingerprint the merged plan instead.
    /// </summary>
    public string Fingerprint() => ModFingerprint.ComputePlan(Files, Result);

    public void ReleaseContents() => ReleaseContents(Files);

    internal static void ReleaseContents(List<PlannedFileOperation> files)
    {
        for (var i = 0; i < files.Count; i++)
            if (files[i].Content != null) files[i] = files[i] with { Content = null };
    }
}
