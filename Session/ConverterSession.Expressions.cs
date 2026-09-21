using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdvancedPenumbraModConverter.Core;
using AdvancedPenumbraModConverter.Services;

namespace AdvancedPenumbraModConverter.Session;

public enum ExpressionSourceKind
{
    /// <summary>The face a game emote plays, e.g. the one /smile carries.</summary>
    Vanilla,

    /// <summary>A facial animation another installed Penumbra mod ships.</summary>
    Mod,
}

/// <summary>A .pap in another mod that carries facial animation.</summary>
public sealed record ModExpression(string Label, string FullPath);

/// <summary>
/// Attaching a facial expression to an animation: on its own, or on top of a swap or retarget.
/// The face comes from a game emote or from another Penumbra mod.
/// </summary>
public sealed partial class ConverterSession
{
    /// <summary>Swap or retarget: also attach the chosen expression.</summary>
    public bool AttachExpression { get; private set; }

    public ExpressionSourceKind ExpressionSource { get; private set; } = ExpressionSourceKind.Vanilla;

    /// <summary>Vanilla: the emote whose face is attached, or 0.</summary>
    public uint ExpressionEmote { get; private set; }

    /// <summary>Mod: the Penumbra mod the face is taken from, or null.</summary>
    public string? ExpressionModDirectory { get; private set; }

    /// <summary>Mod: the chosen .pap, or null.</summary>
    public ModExpression? ExpressionModFile { get; private set; }

    private string? _scannedExpressionMod;
    private IReadOnlyList<ModExpression>? _modExpressions;
    private bool _scanningExpressions;

    public string? ExpressionUnavailableReason => _plugin.Converter.ExpressionUnavailableReason;

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
    /// The facial animations the chosen mod ships, or null while they are being found. A mod is
    /// scanned once, off the framework thread: every .pap it redirects is opened and kept when
    /// it carries at least one facial entry.
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

    private static List<ModExpression> ScanExpressions(string directory)
    {
        var found = new List<ModExpression>();
        try
        {
            var mod = PenumbraMod.Load(directory);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var container in mod.Containers)
            foreach (var (key, local) in container.FileEntries())
            {
                if (!key.EndsWith(".pap", StringComparison.OrdinalIgnoreCase)) continue;
                string full;
                try { full = PathSafety.ResolveRelative(directory, GamePath.ToLocal(local)); }
                catch (InvalidDataException) { continue; }
                if (!seen.Add(full) || !File.Exists(full)) continue;
                try
                {
                    var pap = new PapFile(File.ReadAllBytes(full));
                    if (!pap.FaceEntries.Any()) continue;
                    var name = Path.GetFileNameWithoutExtension(GamePath.Normalize(key));
                    var where = container.Group == null ? string.Empty : $" ({container.Label})";
                    found.Add(new ModExpression($"{name}{where}", full));
                }
                catch (InvalidDataException) { /* not a readable animation */ }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A mod that cannot be read offers nothing.
        }

        return found.OrderBy(e => e.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Why the expression inputs are incomplete, or null.</summary>
    private string? ExpressionBlockReason()
    {
        if (!WantsExpression) return null;
        if (_plugin.Converter.ExpressionUnavailableReason is { } unavailable) return unavailable;
        return ExpressionSource switch
        {
            ExpressionSourceKind.Vanilla when AnimationEmotes == null => "Reading the emote list…",
            ExpressionSourceKind.Vanilla when ExpressionEmote == 0 => "Choose the emote whose expression to attach.",
            ExpressionSourceKind.Mod when ExpressionModDirectory == null => "Choose the mod to take the expression from.",
            ExpressionSourceKind.Mod when ExpressionModFile == null => "Choose the expression in that mod.",
            _ => null,
        };
    }

    /// <summary>The donor the planner reads, for the race the animation plays on.</summary>
    private ExpressionDonor? CurrentExpression(AnimationSource source)
    {
        if (!WantsExpression) return null;
        if (ExpressionSource == ExpressionSourceKind.Mod)
            return ExpressionModFile is { } file
                ? new ExpressionDonor($"{Path.GetFileName(ExpressionModDirectory)}: {file.Label}", FilePath: file.FullPath)
                : null;

        if (GameData.Animations.FindEmote(ExpressionEmote) is not { } emote || emote.Timelines.Length == 0) return null;
        var race = source.Races.Contains((ushort)101) ? (ushort)101 : source.Races.FirstOrDefault();
        // The planner swaps in each animation's own race; this is only the starting point.
        return new ExpressionDonor($"/{emote.Name}",
            GamePath: $"chara/human/c{race:D4}/animation/{emote.Timelines[0].Location}.pap");
    }

    private string ExpressionLabel
        => ExpressionSource == ExpressionSourceKind.Mod
            ? ExpressionModFile?.Label ?? "expression"
            : GameData.Animations.FindEmote(ExpressionEmote) is { } emote ? $"/{emote.Name} face" : "expression";
}
