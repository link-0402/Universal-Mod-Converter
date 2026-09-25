using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UniversalModConverter.Core;
using UniversalModConverter.Services;

namespace UniversalModConverter.Session;

public enum ExpressionSourceKind
{
    /// <summary>A face from the game's Expressions list, e.g. /Smile.</summary>
    Vanilla,

    /// <summary>A facial animation another installed Penumbra mod ships.</summary>
    Mod,
}

/// <summary>A facial expression another mod provides in a face pack, or plays in one of its animations.</summary>
public sealed record ModExpression(string Label, FacialAnimation Face);

/// <summary>
/// Attaching a facial expression to an animation: on its own, or on top of a swap or retarget.
/// The face comes from the game's Expressions list or from another Penumbra mod.
/// </summary>
public sealed partial class ConverterSession
{
    /// <summary>Swap or retarget: also attach the chosen expression.</summary>
    public bool AttachExpression { get; private set; }

    public ExpressionSourceKind ExpressionSource { get; private set; } = ExpressionSourceKind.Vanilla;

    /// <summary>Vanilla: the expression (its Emote row) that is attached, or 0.</summary>
    public uint ExpressionEmote { get; private set; }

    /// <summary>Mod: the Penumbra mod the face is taken from, or null.</summary>
    public string? ExpressionModDirectory { get; private set; }

    /// <summary>Mod: the chosen .pap, or null.</summary>
    public ModExpression? ExpressionModFile { get; private set; }

    private string? _scannedExpressionMod;
    private IReadOnlyList<ModExpression>? _modExpressions;
    private bool _scanningExpressions;

    /// <summary>Whether the current operation will attach an expression.</summary>
    public bool WantsExpression => AnimationOperation == AnimationOperation.Expression || AttachExpression;

    public void SetAttachExpression(bool attach)
    {
        if (attach == AttachExpression) return;
        AttachExpression = attach;
        MarkDirty();
    }

    public void SetExpressionSource(ExpressionSourceKind kind)
    {
        if (kind == ExpressionSource) return;
        ExpressionSource = kind;
        MarkDirty();
    }

    public void SetExpressionEmote(uint id)
    {
        if (id == ExpressionEmote) return;
        ExpressionEmote = id;
        MarkDirty();
    }

    public void SetExpressionMod(string? directory)
    {
        if (string.Equals(directory, ExpressionModDirectory, StringComparison.OrdinalIgnoreCase)) return;
        ExpressionModDirectory = directory;
        ExpressionModFile = null;
        MarkDirty();
    }

    public void SetExpressionModFile(ModExpression? file)
    {
        if (file == ExpressionModFile) return;
        ExpressionModFile = file;
        MarkDirty();
    }

    /// <summary>
    /// The facial expressions the chosen mod offers, or null while they are being found. A mod
    /// is scanned once, off the framework thread: the faces its timelines play (its expression
    /// emotes and animations), each the way it plays it, and the expressions (<c>cfxf_</c>) of
    /// the face packs it replaces.
    /// </summary>
    public IReadOnlyList<ModExpression>? ModExpressions
    {
        get
        {
            var directory = ExpressionModDirectory;
            if (directory == null) return [];
            if (string.Equals(_scannedExpressionMod, directory, StringComparison.OrdinalIgnoreCase)) return _modExpressions;
            if (_scanningExpressions) return null;
            _scanningExpressions = true;
            System.Threading.Tasks.Task.Run(() =>
            {
                var found = ScanExpressions(directory);
                Runner.Post(() =>
                {
                    _scannedExpressionMod = directory;
                    _modExpressions = found;
                    _scanningExpressions = false;
                });
            });
            return null;
        }
    }

    /// <summary><c>chara/human/c0801/animation/f0002/nonresident/smile.pap</c>: a pack of a race's face animations.</summary>
    [GeneratedRegex(@"^chara/human/c\d{4}/animation/f\d{4}/(?<where>resident|nonresident)/(?<name>[^/]+)\.pap$", RegexOptions.IgnoreCase)]
    private static partial Regex FacePack();

    private const string ExpressionPrefix = "cfxf_";

    private static List<ModExpression> ScanExpressions(string directory)
    {
        // How the mod plays each face, by its timelines, and the faces its packs merely hold.
        var played = new Dictionary<FacialAnimation, string>();
        var packed = new Dictionary<FacialAnimation, string>();
        try
        {
            var mod = PenumbraMod.Load(directory);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var container in mod.Containers)
            foreach (var (key, local) in container.FileEntries())
            {
                var isPap = key.EndsWith(".pap", StringComparison.OrdinalIgnoreCase);
                if (!isPap && !key.EndsWith(".tmb", StringComparison.OrdinalIgnoreCase)) continue;
                string full;
                try { full = PathSafety.ResolveRelative(directory, GamePath.ToLocal(local)); }
                catch (InvalidDataException) { continue; }
                if (!seen.Add(full) || !File.Exists(full)) continue;
                try
                {
                    var gamePath = GamePath.Normalize(key);
                    var bytes = File.ReadAllBytes(full);
                    var file = Path.GetFileName(gamePath);
                    var where = container.Group == null ? string.Empty : $", {container.Label}";
                    void Played(TmbTimeline timeline)
                    {
                        foreach (var face in timeline.Faces.Where(f => f.StartsWith(ExpressionPrefix, StringComparison.Ordinal)))
                            played.TryAdd(new FacialAnimation(face, timeline.FacePack, timeline.TimingOf(face)),
                                $"{face[ExpressionPrefix.Length..]} (as {file} plays it{where})");
                    }

                    if (!isPap)
                    {
                        Played(TmbTimeline.Parse(bytes));
                        continue;
                    }

                    var pap = new PapFile(bytes);
                    if (FacePack().Match(gamePath) is { Success: true } pack)
                    {
                        // The game loads a nonresident pack by the name the timeline gives.
                        var name = pack.Groups["where"].Value.Equals("nonresident", StringComparison.OrdinalIgnoreCase)
                            ? pack.Groups["name"].Value
                            : null;
                        foreach (var (entry, _) in pap.FaceEntries.Where(e => e.Entry.Name.StartsWith(ExpressionPrefix, StringComparison.Ordinal)))
                            packed.TryAdd(new FacialAnimation(entry.Name, name), $"{entry.Name[ExpressionPrefix.Length..]} ({file}{where})");
                        continue;
                    }

                    foreach (var (_, index) in pap.BodyEntries)
                    {
                        try { Played(TmbTimeline.Parse(pap.Timeline(index))); }
                        catch (InvalidDataException) { /* a timeline this converter cannot read */ }
                    }
                }
                catch (InvalidDataException) { /* not a readable animation or timeline */ }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A mod that cannot be read offers nothing.
        }

        // A face the mod plays is offered the ways it plays it, which carry its pace; one it only
        // ships plays as the game plays that expression, or holds its first frame.
        var faces = played.Keys.Select(f => f.Entry).ToHashSet(StringComparer.Ordinal);
        return played.Concat(packed.Where(p => !faces.Contains(p.Key.Entry)))
            .Select(f => new ModExpression(f.Value, f.Key))
            .OrderBy(e => e.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Why the expression inputs are incomplete, or null.</summary>
    private string? ExpressionBlockReason()
    {
        if (!WantsExpression) return null;
        return ExpressionSource switch
        {
            ExpressionSourceKind.Vanilla when AnimationExpressions == null => "Reading the expression list…",
            ExpressionSourceKind.Vanilla when GameData.Animations.FindExpression(ExpressionEmote) == null
                => "Choose the expression to attach.",
            ExpressionSourceKind.Mod when ExpressionModDirectory == null => "Choose the mod to take the expression from.",
            ExpressionSourceKind.Mod when ExpressionModFile == null => "Choose the expression in that mod.",
            _ => null,
        };
    }

    /// <summary>The expression the planner attaches; a game pose is looked up for each animation's own race.</summary>
    private ExpressionDonor? CurrentExpression(AnimationSource source)
    {
        if (!WantsExpression) return null;
        if (ExpressionSource == ExpressionSourceKind.Mod)
            return ExpressionModFile is { } file
                ? new ExpressionDonor($"{Path.GetFileName(ExpressionModDirectory)}: {file.Label}", Face: file.Face)
                : null;

        // The planner reads the pose for each animation's own race.
        return GameData.Animations.FindExpression(ExpressionEmote) is { } expression
            ? new ExpressionDonor($"/{expression.Name}", Pose: expression.Pose)
            : null;
    }

    private string ExpressionLabel
        => ExpressionSource == ExpressionSourceKind.Mod
            ? ExpressionModFile?.Label ?? "expression"
            : GameData.Animations.FindExpression(ExpressionEmote) is { } expression ? $"/{expression.Name}" : "expression";
}
