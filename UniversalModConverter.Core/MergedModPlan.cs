namespace UniversalModConverter.Core;

/// <summary>
/// What one conversion of a merged run laid claim to. Two conversions may share a mod, but
/// not the same game path, the same file, or the same item: those are the ways one would
/// silently undo the other.
/// </summary>
public sealed record PlanClaims(
    IReadOnlySet<string> OutputKeys,
    IReadOnlySet<string> RemovedKeys,
    IReadOnlySet<string> Destinations,
    IReadOnlySet<string> ConsumedLocals,
    IReadOnlySet<string> Roots,
    IReadOnlySet<int> TouchedImcGroups);

/// <param name="Rejected">True when this conversion overlapped an earlier one and was undone.</param>
public sealed record MergedPlanEntry(
    string Description,
    IModFilePlan? Plan,
    PlanClaims Claims,
    IReadOnlyList<PlanDiagnostic> Diagnostics,
    bool Rejected);

/// <summary>
/// Several conversions written as one: the file operations of each in turn, over the single
/// mod definition they all built. The executor takes this exactly as it takes a single plan.
/// </summary>
public sealed class MergedModPlan : IModFilePlan
{
    internal MergedModPlan(ModPlanContext context, IReadOnlyList<MergedPlanEntry> entries,
        IReadOnlyList<PlanDiagnostic> diagnostics)
    {
        Mode = context.Mode;
        Result = context.Result;
        Entries = entries;
        Diagnostics = diagnostics;
        Files = entries.Where(e => !e.Rejected && e.Plan != null).SelectMany(e => e.Plan!.Files).ToList();
        InputFiles = new HashSet<string>(context.InputFiles, StringComparer.OrdinalIgnoreCase);
    }

    public ConversionOutputMode Mode { get; }

    public PenumbraMod Result { get; }

    public IReadOnlyList<PlannedFileOperation> Files { get; }

    public IReadOnlyList<MergedPlanEntry> Entries { get; }

    public IReadOnlyList<PlanDiagnostic> Diagnostics { get; }

    public HashSet<string> InputFiles { get; }

    /// <summary>A rejected entry blocks the whole run: half a queue is not what was asked for.</summary>
    public bool HasBlockers => Diagnostics.Any(d => d.IsBlocker) || Entries.Any(e => e.Rejected);

    /// <summary>
    /// Deterministic hash of the whole run. The entry descriptions are mixed in, so reordering
    /// or renaming the queue invalidates a preview even when the file operations come out the same.
    /// </summary>
    public string Fingerprint()
        => ModFingerprint.ComputePlan(Files, Result, string.Join("\u001f", Entries.Select(e => e.Description)));
}

/// <summary>
/// Plans several conversions into one mod. Each reads the untouched original and writes into
/// the shared result; before each one the definition is snapshotted, and a conversion that
/// turns out to overlap an accepted one is rolled back and reported instead of being allowed
/// to half-apply.
/// </summary>
public sealed class ModPlanMerger(ModPlanContext context)
{
    private readonly List<MergedPlanEntry> _entries = [];
    private readonly List<PlanDiagnostic> _diagnostics = [];

    public ModPlanContext Context { get; } = context;

    /// <param name="roots">
    /// What this conversion is about, in whatever terms its kind uses: item roots for gear,
    /// animation locations for animations. Two conversions claiming a root is the overlap a
    /// user can actually recognise, so it is checked before the low-level rules.
    /// </param>
    public MergedPlanEntry Add(string description, IEnumerable<string> roots, Func<ModPlanContext, IModFilePlan> plan)
    {
        var before  = Context.Result.Snapshot();
        var keys    = CaptureKeys(Context.Result);
        var groups  = CaptureImcGroups(Context.Result);

        IModFilePlan planned;
        List<PlanDiagnostic> diagnostics;
        try
        {
            planned = plan(Context);
            diagnostics = [.. Diagnostics(planned)];
        }
        catch (Exception ex)
        {
            Context.Result.Restore(before);
            return Record(new MergedPlanEntry(description, null, Empty(roots),
                [new PlanDiagnostic("planning_failed", $"{description}: {ex.Message}", true)], Rejected: true));
        }

        var claims = Claims(planned, keys, groups, roots);
        if (Conflict(description, claims) is { } conflict)
        {
            Context.Result.Restore(before);
            diagnostics.Add(conflict);
            _diagnostics.Add(conflict);
            return Record(new MergedPlanEntry(description, planned, claims, diagnostics, Rejected: true));
        }

        // A conversion that cannot run must not leave half its edits in the shared definition.
        if (planned.HasBlockers)
        {
            Context.Result.Restore(before);
            return Record(new MergedPlanEntry(description, planned, claims, diagnostics, Rejected: true));
        }

        return Record(new MergedPlanEntry(description, planned, claims, diagnostics, Rejected: false));
    }

    public MergedModPlan Build()
    {
        Validate();
        return new MergedModPlan(Context, _entries, _diagnostics);
    }

    private MergedPlanEntry Record(MergedPlanEntry entry)
    {
        _entries.Add(entry);
        return entry;
    }

    private static IEnumerable<PlanDiagnostic> Diagnostics(IModFilePlan plan) => plan switch
    {
        GearConversionPlan gear           => gear.Diagnostics,
        AnimationConversionPlan animation => animation.Diagnostics,
        _                                 => [],
    };

    // ── Conflicts ────────────────────────────────────────────────────────────

    private PlanDiagnostic? Conflict(string description, PlanClaims claims)
    {
        foreach (var other in _entries.Where(e => !e.Rejected))
        {
            // Claiming the same item is the overlap worth naming first: it is the one the user
            // can see in the queue without knowing anything about game paths.
            if (Overlap(claims.Roots, other.Claims.Roots) is { } root)
                return Reject(description, other.Description, "convert the same item", root);
            if (Overlap(claims.OutputKeys, other.Claims.OutputKeys) is { } key)
                return Reject(description, other.Description, "redirect the same game path", Path(key));
            if (Overlap(claims.RemovedKeys, other.Claims.RemovedKeys) is { } removed)
                return Reject(description, other.Description, "take over the same game path", Path(removed));
            if (Overlap(claims.Destinations, other.Claims.Destinations) is { } destination)
                return Reject(description, other.Description, "write the same file", destination);
            var crossed = Overlap(claims.Destinations, other.Claims.ConsumedLocals)
                          ?? Overlap(claims.ConsumedLocals, other.Claims.Destinations);
            if (crossed != null)
                return Reject(description, other.Description,
                    "use the same file, one writing it and one moving it away", crossed);
            if (Overlap(claims.ConsumedLocals, other.Claims.ConsumedLocals) is { } consumed)
                return Reject(description, other.Description, "move or delete the same file", consumed);
            if (claims.TouchedImcGroups.Overlaps(other.Claims.TouchedImcGroups))
                return Reject(description, other.Description, "change the same IMC option group",
                    claims.TouchedImcGroups.First(g => other.Claims.TouchedImcGroups.Contains(g)).ToString());
        }

        return null;
    }

    private static PlanDiagnostic Reject(string description, string other, string what, string example)
        => new("queue_conflict",
            $"'{description}' and '{other}' both {what} ({example}), so they cannot be converted together. " +
            "Remove one of them, or convert them one after the other.", true);

    private static string? Overlap<T>(IReadOnlySet<T> left, IReadOnlySet<T> right) where T : notnull
        => left.FirstOrDefault(right.Contains)?.ToString();

    /// <summary>Strips the container prefix a captured key carries, leaving the game path.</summary>
    private static string Path(string capturedKey)
    {
        var bar = capturedKey.IndexOf('|');
        return bar < 0 ? capturedKey : capturedKey[(bar + 1)..];
    }

    /// <summary>
    /// A last check over the finished run: the executor replays operations in order and would
    /// throw or overwrite silently, so an overlap that slipped past the per-entry rules is
    /// caught here rather than mid-write.
    /// </summary>
    private void Validate()
    {
        var accepted = _entries.Where(e => !e.Rejected && e.Plan != null).ToList();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in accepted.SelectMany(e => e.Plan!.Files))
        {
            var destination = GamePath.NormalizeLocal(operation.Destination);
            if (operation.Operation is LocalFileOperation.Move or LocalFileOperation.Delete)
                consumed.Add(GamePath.NormalizeLocal(operation.Source ?? operation.Destination));
            if (operation.Operation == LocalFileOperation.Delete) continue;
            if (!written.Add(destination))
                _diagnostics.Add(new PlanDiagnostic("queue_conflict",
                    $"Two conversions in this run both write {operation.Destination}.", true));
        }

        foreach (var clash in written.Intersect(consumed, StringComparer.OrdinalIgnoreCase))
            _diagnostics.Add(new PlanDiagnostic("queue_conflict",
                $"One conversion in this run writes {clash} while another moves or deletes it.", true));
    }

    // ── Claims ───────────────────────────────────────────────────────────────

    private static PlanClaims Empty(IEnumerable<string> roots)
        => new(new HashSet<string>(), new HashSet<string>(), new HashSet<string>(), new HashSet<string>(),
            roots.ToHashSet(StringComparer.OrdinalIgnoreCase), new HashSet<int>());

    private PlanClaims Claims(IModFilePlan plan, Dictionary<string, string> before,
        Dictionary<int, string> beforeGroups, IEnumerable<string> roots)
    {
        var after = CaptureKeys(Context.Result);
        var output = after.Where(e => !before.TryGetValue(e.Key, out var value) || value != e.Value)
            .Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = before.Keys.Where(key => !after.ContainsKey(key)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in plan.Files)
        {
            if (operation.Operation is LocalFileOperation.Move or LocalFileOperation.Delete)
                consumed.Add(GamePath.NormalizeLocal(operation.Source ?? operation.Destination));
            if (operation.Operation != LocalFileOperation.Delete)
                destinations.Add(GamePath.NormalizeLocal(operation.Destination));
        }

        var afterGroups = CaptureImcGroups(Context.Result);
        var touched = afterGroups
            .Where(g => !beforeGroups.TryGetValue(g.Key, out var node) || node != g.Value)
            .Select(g => g.Key).ToHashSet();

        return new PlanClaims(output, removed, destinations, consumed,
            roots.ToHashSet(StringComparer.OrdinalIgnoreCase), touched);
    }

    /// <summary>Every redirect the definition holds, addressed by the container it lives in.</summary>
    private static Dictionary<string, string> CaptureKeys(PenumbraMod mod)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var container in mod.Containers)
        {
            var scope = $"{container.Address.Group}:{container.Address.Index}|";
            foreach (var (key, local) in container.FileEntries()) keys[scope + GamePath.Normalize(key)] = local;
            foreach (var (key, target) in container.SwapEntries()) keys[scope + GamePath.Normalize(key)] = target;
        }

        return keys;
    }

    /// <summary>
    /// IMC groups have no containers, so their contents never show up as keys. They carry one
    /// item identifier each, which is exactly why two conversions cannot share one.
    /// </summary>
    private static Dictionary<int, string> CaptureImcGroups(PenumbraMod mod)
    {
        var groups = new Dictionary<int, string>();
        for (var i = 0; i < mod.Groups.Count; i++)
            if (mod.Groups[i].IsImc)
                groups[i] = PenumbraMod.Serialize(mod.Groups[i].Node);
        return groups;
    }
}
