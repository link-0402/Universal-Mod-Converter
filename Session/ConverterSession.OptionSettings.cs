using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniversalModConverter.Core;
using UniversalModConverter.Services;

namespace UniversalModConverter.Session;

/// <summary>
/// Reloading a mod in Penumbra while keeping what each collection chose for it.
/// <para>
/// Penumbra keeps a collection's choices for a mod as one value per group, by position: an index
/// for a single-select group, a mask for a multi-select one. A reload only fits each value to
/// the group now at its position. So a group that became multi-select reads its index as a mask
/// (the wrong options on, or none), an option added to a group a collection has settings for
/// starts off, and a mod that now has fewer groups than a collection has values for makes
/// Penumbra (1.7) throw while reloading it, which leaves the stale values in place and makes every
/// later reload of the mod throw too. The choices are therefore read by name before the mod
/// changes on disk. When the mod loses groups, or a reload fails anyway, the collections' own
/// settings are set aside (the mod inherits for a moment), the mod is reloaded, and they are put
/// back by name.
/// </para>
/// </summary>
public sealed partial class ConverterSession
{
    /// <summary>A collection's own settings for a mod: whether it is on, its priority, and the options on per group.</summary>
    private sealed record OwnSettings(Guid Collection, bool Enabled, int Priority, Dictionary<string, List<string>> Groups);

    /// <summary>The settings read before the mod changed, and whether they were set aside for a reload.</summary>
    private sealed class KeptSettings(string folder, List<OwnSettings> settings, int groups)
    {
        public string Folder { get; } = folder;
        public List<OwnSettings> Settings { get; } = settings;

        /// <summary>How many groups the mod had when they were read.</summary>
        public int Groups { get; } = groups;

        public bool SetAside { get; set; }
    }

    /// <summary>Every collection's own settings for the mod in <paramref name="directory"/>, read before it changes on disk.</summary>
    private KeptSettings CaptureSettings(string directory)
    {
        var folder = ModFolder(directory);
        var settings = new List<OwnSettings>();
        if (PenumbraAvailable && _plugin.PenumbraIpc.GetCollections() is { } collections)
            foreach (var collection in collections.Keys)
                if (_plugin.PenumbraIpc.GetOwnModSettings(collection, folder) is { } own)
                    settings.Add(new OwnSettings(collection, own.Enabled, own.Priority, own.Groups));
        return new KeptSettings(folder, settings, GroupCount(directory) ?? 0);
    }

    private static int? GroupCount(string directory)
    {
        try { return PenumbraMod.Load(directory).Groups.Count; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Reloads the mod, setting the collections' own settings aside first when it has fewer groups
    /// than they were read with, or when reloading it with them fails.
    /// </summary>
    private bool ReloadKeepingSettings(string directory, KeptSettings kept)
    {
        var shrinks = GroupCount(directory) is { } now && now < kept.Groups;
        if ((!shrinks || kept.Settings.Count == 0 || kept.SetAside) && _plugin.PenumbraIpc.ReloadMod(kept.Folder)) return true;
        if (kept.Settings.Count == 0 || kept.SetAside) return false;

        Log.Add(shrinks
            ? $"The mod has fewer option groups now, so the settings of {kept.Settings.Count} collection(s) are set aside while Penumbra reloads it."
            : $"Penumbra could not reload the mod with the settings {kept.Settings.Count} collection(s) have for it; they are set aside and it is reloaded again.");
        foreach (var settings in kept.Settings)
            _plugin.PenumbraIpc.TryInheritMod(settings.Collection, kept.Folder, true);
        kept.SetAside = true;
        return _plugin.PenumbraIpc.ReloadMod(kept.Folder);
    }

    /// <summary>
    /// Once Penumbra has reloaded the mod: puts settings that were set aside back, and fits the
    /// groups <paramref name="adjust"/> names (group, options on before → options on now, or null
    /// to leave a group as it is).
    /// </summary>
    private void PutSettingsBack(KeptSettings kept, Func<string, List<string>, List<string>?> adjust)
    {
        var failed = new List<string>();
        var adjusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var settings in kept.Settings)
        {
            if (kept.SetAside)
            {
                _plugin.PenumbraIpc.TryInheritMod(settings.Collection, kept.Folder, false);
                _plugin.PenumbraIpc.TrySetMod(settings.Collection, kept.Folder, settings.Enabled);
                _plugin.PenumbraIpc.TrySetModPriority(settings.Collection, kept.Folder, settings.Priority);
            }
            foreach (var (group, on) in settings.Groups)
            {
                var wanted = adjust(group, on);
                if (wanted != null) adjusted.Add(group);
                else if (!kept.SetAside) continue;
                // A group or option the mod no longer has is nothing to put back.
                var rc = _plugin.PenumbraIpc.TrySetModSettings(settings.Collection, kept.Folder, group, wanted ?? on);
                if (rc is not (PenumbraApiEc.Success or PenumbraApiEc.NothingDone or PenumbraApiEc.OptionGroupMissing or PenumbraApiEc.OptionMissing))
                    failed.Add(group);
            }
        }

        if (kept.Settings.Count == 0 || !kept.SetAside && adjusted.Count == 0) return;
        if (failed.Count > 0)
            Log.Add(LogLevel.Warning, $"Penumbra did not take back every setting; check {string.Join(", ", failed.Distinct().Select(g => $"'{g}'"))} " +
                                      "in the mod's settings in Penumbra.");
        else
            Log.Add(kept.SetAside
                ? $"Put the settings of {kept.Settings.Count} collection(s) back."
                : $"Kept the choices of {kept.Settings.Count} collection(s) in {string.Join(", ", adjusted.Select(g => $"'{g}'"))}.");
    }

    /// <summary>After adding: the converted items' options are switched on beside what each collection had on.</summary>
    private static Func<string, List<string>, List<string>?> AddOptions(IReadOnlyList<ConvertedOptionPlacement> placements)
        => (group, on) =>
        {
            var added = placements.Where(p => !p.NewGroup && string.Equals(p.Group, group, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Option).ToList();
            return added.Count == 0 ? null : [.. on.Concat(added).Distinct(StringComparer.OrdinalIgnoreCase)];
        };

    /// <summary>
    /// After reverting: the converted items' options are gone again; a group that was
    /// single-select keeps one choice, or Penumbra's default when none is left.
    /// </summary>
    private static Func<string, List<string>, List<string>?> RemoveOptions(IReadOnlyList<ConvertedOptionPlacement> placements)
        => (group, on) =>
        {
            var removed = placements.Where(p => string.Equals(p.Group, group, StringComparison.OrdinalIgnoreCase)).ToList();
            if (removed.Count == 0 || removed.Any(p => p.NewGroup)) return null;
            var names = removed.Select(p => p.Option).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<string> left = [.. on.Where(o => !names.Contains(o))];
            if (!removed.Any(p => p.TurnedMulti)) return left;
            return left.Count == 0 ? null : [left[0]];
        };
}
