namespace UniversalModConverter.Core;

/// <summary>Adds animation bindings from one Havok container to another.</summary>
public interface IExpressionMerger
{
    /// <summary>Why containers cannot be merged in this game build, or null.</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Returns <paramref name="target"/> with the donor's <paramref name="donorBindings"/> (and
    /// their animations) appended, in that order, plus how many bindings the target had before.
    /// </summary>
    (byte[] Havok, int OriginalBindings) Append(byte[] target, byte[] donor, IReadOnlyList<int> donorBindings);
}

/// <summary>Where the facial expression to attach comes from.</summary>
/// <param name="Label">What the user picked, for messages: "/smile", "Some Mod: smile.pap".</param>
/// <param name="GamePath">A vanilla .pap, read from the game.</param>
/// <param name="FilePath">A .pap on disk, typically inside another Penumbra mod.</param>
public sealed record ExpressionDonor(string Label, string? GamePath = null, string? FilePath = null);

/// <summary>
/// Attaches a facial expression to an animation. FFXIV keeps a pose's face as extra entries
/// in the same .pap, marked with the face type they are for; the game plays the one matching
/// the character's face alongside the body animation of the same name. So attaching means
/// copying the donor's facial entries, with their Havok animations and timelines, into the
/// target and naming them after the target's body animation.
/// </summary>
public static class PapExpressions
{
    public static byte[] Attach(byte[] target, byte[] donor, IExpressionMerger merger, List<string> notes)
    {
        if (merger.UnavailableReason is { } reason) throw new InvalidOperationException(reason);
        var targetPap = new PapFile(target);
        var donorPap  = new PapFile(donor);

        var body = targetPap.BodyEntries.ToList();
        if (body.Count == 0) throw new InvalidDataException("The animation has no body animation to attach a face to.");
        var faces = donorPap.FaceEntries.ToList();
        if (faces.Count == 0) throw new InvalidDataException("The chosen expression has no facial animation.");

        // A face type the target already animates keeps its own animation: two entries for the
        // same face and name would leave it to chance which one plays.
        var existing = targetPap.FaceEntries.Select(e => e.Entry.Face).ToHashSet();
        var skipped  = faces.Where(f => existing.Contains(f.Entry.Face)).Select(f => f.Entry.Face).Distinct().ToList();
        if (skipped.Count > 0)
            notes.Add($"The animation already has a face for face type(s) {string.Join(", ", skipped)}; those keep it.");
        faces = faces.Where(f => !existing.Contains(f.Entry.Face)).ToList();
        if (faces.Count == 0)
            throw new InvalidDataException("The animation already has a facial animation for every face the expression covers.");

        // Donor facial entries are named after the donor's body animations; pair them with the
        // target's body animations by position so the game plays them together.
        var donorBody = donorPap.BodyEntries.Select(e => e.Entry.Name).ToList();
        string NameFor(string donorName)
        {
            var position = donorBody.IndexOf(donorName);
            return body[position >= 0 && position < body.Count ? position : 0].Entry.Name;
        }

        var bindings = faces.Select(f => (int)f.Entry.Binding).Distinct().ToList();
        var (havok, original) = merger.Append(targetPap.Havok, donorPap.Havok, bindings);

        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        var appended = new List<(PapFile.Entry, byte[])>();
        foreach (var (entry, index) in faces)
        {
            var name = NameFor(entry.Name);
            if (name != entry.Name) renames[entry.Name] = name;
            var binding = (short)(original + bindings.IndexOf(entry.Binding));
            appended.Add((entry with { Name = name, Binding = binding }, donorPap.Timeline(index)));
        }

        var result = targetPap.WithAppendedEntries(havok, appended);
        // The copied timelines still name the donor's animation; point them at the new names.
        // Existing timelines never name a donor animation, so only the copies change.
        var stale = renames.Where(r => PapTimeline.ReadStrings(result).Any(s => s.IsMotion && s.Value == r.Key))
            .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
        if (stale.Count > 0) result = PapTimeline.RenameMotions(result, stale);

        notes.Add($"Attached {faces.Count} facial animation(s) for face type(s) " +
                  $"{string.Join(", ", faces.Select(f => f.Entry.Face).Distinct().Order())}.");
        return result;
    }
}
