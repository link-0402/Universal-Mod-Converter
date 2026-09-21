namespace AdvancedPenumbraModConverter.Core;

/// <summary>
/// The one mod snapshot, one result definition and one file-name allocator that a planning
/// run shares.
/// <para>
/// A single conversion owns all three outright, which is what the planners assumed. Queuing
/// several conversions into one run does not change what any of them reads — every entry
/// still plans against <see cref="Source"/>, the untouched original — but they must all write
/// into the same <see cref="Result"/> and draw local file names from the same allocator, or
/// the last entry silently overwrites the others.
/// </para>
/// </summary>
public sealed class ModPlanContext
{
    private readonly Dictionary<string, Action<PenumbraMod>> _finalizers = new(StringComparer.Ordinal);
    private readonly List<string> _finalizerOrder = [];
    private bool _finalized;

    /// <param name="shared">
    /// True when several conversions write into this context. The allocator then never hands a
    /// name back, so one entry cannot reserve a file another entry is about to move away.
    /// </param>
    public ModPlanContext(string modDirectory, ConversionOutputMode mode, bool shared = false)
    {
        ModDirectory = Path.GetFullPath(modDirectory);
        Mode = mode;
        Shared = shared;
        Source = PenumbraMod.Load(ModDirectory);
        Result = mode.IsNewMod() ? Source.CloneStructure() : Source.Clone();
        Locals = new GearConversionPlanner.LocalAllocator(mode.IsNewMod() ? null : ModDirectory, reuseReleased: !shared);
        foreach (var file in PenumbraMod.DefinitionFiles(ModDirectory).Where(File.Exists))
            InputFiles.Add(Path.GetFullPath(file));
    }

    public string ModDirectory { get; }

    public ConversionOutputMode Mode { get; }

    public bool Shared { get; }

    /// <summary>The mod as it is on disk. Every entry reads from this and none may change it.</summary>
    public PenumbraMod Source { get; }

    /// <summary>The definition being built. Entries write into this one, in order.</summary>
    public PenumbraMod Result { get; }

    /// <summary>Absolute paths of every source file the run was derived from.</summary>
    public HashSet<string> InputFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal GearConversionPlanner.LocalAllocator Locals { get; }

    /// <summary>
    /// Registers work that finishes the whole mod rather than one entry — pruning empty groups,
    /// stamping a new identifier, deleting orphaned files. Running these per entry would undo
    /// the next entry's work, so they run once, after every entry has planned.
    /// </summary>
    internal void AddFinalizerOnce(string key, Action<PenumbraMod> finalize)
    {
        if (_finalizers.TryAdd(key, finalize)) _finalizerOrder.Add(key);
    }

    /// <summary>Runs the registered finalizers, once. Safe to call again.</summary>
    public void RunFinalizers()
    {
        if (_finalized) return;
        _finalized = true;
        foreach (var key in _finalizerOrder) _finalizers[key](Result);
    }
}
