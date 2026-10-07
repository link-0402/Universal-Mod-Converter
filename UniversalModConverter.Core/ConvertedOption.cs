using System.Text.Json.Nodes;

namespace UniversalModConverter.Core;

/// <summary>The option a conversion added to this mod puts its new files into: "Miqo'te Female".</summary>
public sealed record ConvertedOptionRequest(string Name, string Description);

/// <summary>
/// Where the converted item's option went, for carrying the mod's settings in Penumbra across:
/// a collection saves a single-select group's choice as an index and a multi-select one's as a
/// mask, and Penumbra does not translate one into the other when a reloaded mod's group changed
/// type, nor tick an option added to a group a collection already has settings for.
/// </summary>
/// <param name="Group">The group's name, as Penumbra knows it.</param>
/// <param name="Option">The option's name.</param>
/// <param name="TurnedMulti">The group was single-select, with only the original's option, and is now multi-select.</param>
/// <param name="NewGroup">The group is new, so every collection starts it on its defaults (the option on).</param>
public sealed record ConvertedOptionPlacement(string Group, string Option, bool TurnedMulti, bool NewGroup);

/// <summary>
/// What a conversion added to this mod with an option of its own for the converted item.
/// </summary>
/// <param name="Label">"Converted / Miqo'te Female", the option the new files went into; null when nothing moved.</param>
/// <param name="MovedFiles">Where each moved file was added first, and its game path.</param>
/// <param name="Refusal">Why nothing moved although the conversion made new files.</param>
/// <param name="Kept">When some moved: why the others stay beside the source's, or null when none do.</param>
public sealed record ConvertedOptionOutcome(
    string? Label,
    ContainerAddress Option,
    IReadOnlyList<(ContainerAddress From, string GamePath)> MovedFiles,
    int Manipulations,
    string? Refusal,
    string? Kept = null,
    ConvertedOptionPlacement? Placement = null)
{
    public static ConvertedOptionOutcome Nothing { get; } = new(null, ContainerAddress.Default, [], 0, null);

    public bool Moved => Label != null;
}

/// <summary>
/// Adding to this mod with the converted item in an option of its own. A conversion added to the
/// mod puts the target's paths beside the source's, in the same options. Most of them load the
/// mod's own files (textures, shared materials); those stay there, so the mod's options keep
/// choosing them. What the conversion made new, though (a converted model, a copy it had to patch,
/// a file it took from the game) and the metadata it added for the target belong to the converted
/// item alone: they move into an option of their own beside the original's, so the converted item
/// is switched on and off by itself. That option goes into the group whose option holds the
/// original's model: a multi-select group (a toggle per item) as it is, a single-select one that
/// has only that option turned into a multi-select one. When the model is in Default, or in a
/// group that chooses between several options, it goes into the "Converted" group instead, which
/// a later conversion adds its option to as well.
/// <para>
/// The options that have a new file must all load the same one. The converted model then moves
/// unless only some choices of a single-select group load it (it would load with the others too);
/// the option takes over from a multi-select option that toggled it. Every other new file, and
/// the metadata, moves where it loaded whenever the model did anyway: in Default, in every option
/// of a single-select group, or only beside the model. What the mod's options choose (a material
/// per colour option, a model per variant) stays in them, like the paths to the mod's own files.
/// </para>
/// </summary>
public static class ConvertedOption
{
    public const string GroupName = "Converted";

    private const string GroupDescription =
        "One option per conversion added to this mod: the files made for its target, and its metadata. " +
        "Created by Universal Mod Converter.";

    private const int MaxMultiOptions = 32;

    /// <summary>
    /// Moves what the conversion added to <paramref name="after"/> and made new into an option
    /// named <paramref name="optionName"/>. <paramref name="before"/> is the mod as it was before
    /// the conversion, with the same groups (a conversion only appends groups), and
    /// <paramref name="newFiles"/> the local files it creates, normalized with
    /// <see cref="GamePath.NormalizeLocal"/>. Nothing moves when nothing is new, or when no
    /// converted model can move; the outcome then says why.
    /// </summary>
    public static ConvertedOptionOutcome Move(PenumbraMod before, PenumbraMod after, IReadOnlySet<string> newFiles,
        string optionName, string optionDescription)
    {
        // What the conversion added: paths an option did not have, loading a file it made.
        var files = new List<(ModContainer Container, string Key, string Local)>();
        var manipulations = new List<(ModContainer Container, JsonObject Manipulation)>();
        foreach (var container in after.Containers)
        {
            if (Original(before, container) is not { } original) continue; // a group the conversion added itself
            var had = original.FileEntries().Select(e => GamePath.Normalize(e.Key)).ToHashSet(StringComparer.Ordinal);
            foreach (var (key, local) in container.FileEntries())
                if (!had.Contains(GamePath.Normalize(key)) && newFiles.Contains(GamePath.NormalizeLocal(local)))
                    files.Add((container, key, local));

            var identities = (original.Manipulations?.OfType<JsonObject>() ?? [])
                .Select(GearManipulations.Identity).ToHashSet(StringComparer.Ordinal);
            foreach (var manipulation in container.Manipulations?.OfType<JsonObject>() ?? [])
                if (!identities.Contains(GearManipulations.Identity(manipulation)))
                    manipulations.Add((container, manipulation));
        }
        if (files.Count == 0) return ConvertedOptionOutcome.Nothing;

        var loaded = new Dictionary<string, List<(ModContainer Container, string Local)>>(StringComparer.Ordinal);
        var swapped = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in after.Containers)
        {
            foreach (var (key, local) in container.FileEntries())
            {
                var path = GamePath.Normalize(key);
                if (!loaded.TryGetValue(path, out var list)) loaded[path] = list = [];
                list.Add((container, GamePath.NormalizeLocal(local)));
            }
            foreach (var (key, _) in container.SwapEntries()) swapped.Add(GamePath.Normalize(key));
        }

        // Models first: the option takes over from whatever switched the model on. That may be a
        // multi-select option (a toggle for the item), but not a choice among others that do not
        // load it, which would then load it too.
        var moving = new HashSet<string>(StringComparer.Ordinal);
        var kept = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var paths = files.Select(f => GamePath.Normalize(f.Key)).Distinct(StringComparer.Ordinal).ToList();
        foreach (var path in paths.Where(IsModel))
            if (Blocker(path) is { } reason) kept[path] = reason;
            else if (!LoadsWhateverIsChosen(loaded[path].Select(h => h.Container)) &&
                     !loaded[path].All(h => h.Container.Group?.Type.Equals("Multi", StringComparison.OrdinalIgnoreCase) == true))
                kept[path] = "only some of the mod's options load it";
            else moving.Add(path);

        // Everything else goes along where it loads whenever the model does anyway.
        var withModel = files.Where(f => moving.Contains(GamePath.Normalize(f.Key))).Select(f => f.Container)
            .ToHashSet(ReferenceEqualityComparer.Instance);
        bool GoesAlong(IEnumerable<ModContainer> holders)
        {
            var list = holders.ToList();
            return LoadsWhateverIsChosen(list) || list.All(withModel.Contains);
        }
        foreach (var path in paths.Where(p => !IsModel(p)))
            if (Blocker(path) is { } reason) kept[path] = reason;
            else if (!GoesAlong(loaded[path].Select(h => h.Container))) kept[path] = "only some of the mod's options load it";
            else moving.Add(path);

        // An option of its own is for the converted item, so it needs the converted model.
        if (!moving.Any(IsModel))
            return ConvertedOptionOutcome.Nothing with
            {
                Refusal = kept.FirstOrDefault(k => IsModel(k.Key)) is { Key: { } model } entry
                    ? $"its model, {model}, stays in the options that have it, since {entry.Value}."
                    : "the conversion made no new model.",
            };

        // Metadata: options override Default, so an option's entry is the one the converted item goes
        // with (Default only holds the fallback for when no option sets one). Entries the mod's
        // options choose between stay with them.
        var chosen = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var identity in manipulations.Select(m => GearManipulations.Identity(m.Manipulation)).Distinct(StringComparer.Ordinal))
        {
            var holders = after.Containers
                .SelectMany(c => (c.Manipulations?.OfType<JsonObject>() ?? [])
                    .Where(m => GearManipulations.Identity(m) == identity)
                    .Select(m => (Container: c, Manipulation: m)))
                .ToList();
            var fromOptions = holders.Where(h => !h.Container.Address.IsDefault).ToList();
            var candidates = fromOptions.Count > 0 ? fromOptions : holders;
            if (candidates.Select(h => h.Manipulation.ToJsonString()).Distinct(StringComparer.Ordinal).Count() > 1 ||
                !GoesAlong(candidates.Select(h => h.Container)))
                continue;
            chosen[identity] = candidates[0].Manipulation;
        }

        var moved = files.Where(f => moving.Contains(GamePath.Normalize(f.Key))).ToList();
        var home = HomeGroup(moved.Where(f => IsModel(GamePath.Normalize(f.Key))).Select(f => f.Container));
        var movedMeta = manipulations.Where(m => chosen.ContainsKey(GearManipulations.Identity(m.Manipulation))).ToList();
        foreach (var (container, key, _) in moved) container.Files!.Remove(key);
        foreach (var (container, manipulation) in movedMeta) container.Manipulations!.Remove(manipulation);

        var optionFiles = new JsonObject();
        foreach (var (_, key, local) in moved.OrderBy(f => GamePath.Normalize(f.Key), StringComparer.Ordinal))
            if (GamePath.FindKey(optionFiles, key) == null) optionFiles[key] = local;
        var option = ModGroupBuilder.Option(optionName, optionDescription, optionFiles);
        var optionManipulations = (JsonArray)option["Manipulations"]!;
        foreach (var manipulation in chosen.Values) optionManipulations.Add(manipulation.DeepClone());

        var (label, address, placement) = home is { } group
            ? AddBeside(after, group, option, optionName)
            : AddToConvertedGroup(after, option, optionName);
        string? note = null;
        if (kept.Count > 0)
        {
            var (path, reason) = kept.First();
            note = (kept.Count == 1 ? $"{path} stays" : $"{kept.Count} new files stay, such as {path},") +
                   $" beside the original's, in the options that have it, since {reason}.";
        }
        return new ConvertedOptionOutcome(label, address,
            [.. moved.Select(f => (f.Container.Address, GamePath.Normalize(f.Key))).Distinct()], chosen.Count, null, note,
            placement);

        static bool IsModel(string path) => path.EndsWith(".mdl", StringComparison.Ordinal);

        // One option holds one file per path, and a swap of the same path would compete with it.
        string? Blocker(string path)
            => loaded[path].Select(h => h.Local).Distinct(StringComparer.Ordinal).Count() > 1
                ? "the mod's options each load a file of their own for it"
                : swapped.Contains(path) ? "it is also swapped to another file in this mod" : null;
    }

    /// <summary>
    /// Whether something these containers hold loads whatever the mod's options are set to: it is
    /// in Default, or in every option of a single-select group, one of which is always chosen.
    /// </summary>
    private static bool LoadsWhateverIsChosen(IEnumerable<ModContainer> holders)
    {
        var list = holders.ToList();
        if (list.Any(c => c.Address.IsDefault)) return true;
        return list.Where(c => c.Group is { } g && g.Type.Equals("Single", StringComparison.OrdinalIgnoreCase))
            .GroupBy(c => c.Group!)
            .Any(g => g.Select(c => c.Address.Index).Distinct().Count() == g.Key.Containers.Count);
    }

    /// <summary>The container of <paramref name="before"/> at the same address, or null for one it does not have.</summary>
    private static ModContainer? Original(PenumbraMod before, ModContainer container)
    {
        var address = container.Address;
        if (address.IsDefault) return before.Default;
        if (address.Group >= before.Groups.Count) return null;
        var group = before.Groups[address.Group];
        return address.Index < group.Containers.Count ? group.Containers[address.Index] : null;
    }

    /// <summary>
    /// The group the original's model is switched on by, when the converted item's option can go
    /// beside it: a multi-select group, or a single-select one with only that option. Null when
    /// the model is in Default, in several groups, or in a group that chooses between options.
    /// </summary>
    private static ModGroup? HomeGroup(IEnumerable<ModContainer> holders)
    {
        var groups = holders.Select(h => h.Group).Distinct().ToList();
        if (groups is not [{ } group]) return null;
        if (group.Type.Equals("Multi", StringComparison.OrdinalIgnoreCase))
            return group.Containers.Count < MaxMultiOptions ? group : null;
        return group.Type.Equals("Single", StringComparison.OrdinalIgnoreCase) && group.Containers.Count == 1 ? group : null;
    }

    /// <summary>
    /// Adds the option beside the original's in <paramref name="group"/>, switched on; a
    /// single-select group becomes a multi-select one, keeping its choice switched on.
    /// </summary>
    private static (string Label, ContainerAddress Address, ConvertedOptionPlacement Placement) AddBeside(
        PenumbraMod mod, ModGroup group, JsonObject option, string name)
    {
        var turned = !group.Type.Equals("Multi", StringComparison.OrdinalIgnoreCase);
        if (turned)
        {
            // A single-select group's default is an index; a multi-select one's is a mask.
            var chosen = Math.Clamp(Json.GetInt(group.Node["DefaultSettings"], 0), 0, Math.Max(0, group.Containers.Count - 1));
            group.Node["Type"] = "Multi";
            group.Node["DefaultSettings"] = 1UL << chosen;
            foreach (var existing in group.Options)
                if (existing["Priority"] == null) existing["Priority"] = 0;
        }
        option["Priority"] = 0;
        var (label, address) = Append(mod, group.Index, option, name);
        return (label, address, new ConvertedOptionPlacement(group.Name, Json.GetString(option["Name"])!, turned, false));
    }

    /// <summary>Adds the option to the group an earlier conversion made, or to a new one, switched on.</summary>
    private static (string Label, ContainerAddress Address, ConvertedOptionPlacement Placement) AddToConvertedGroup(
        PenumbraMod mod, JsonObject option, string name)
    {
        var index = mod.Groups.FindIndex(g => g.Name.Equals(GroupName, StringComparison.OrdinalIgnoreCase) &&
                                              g.Type.Equals("Multi", StringComparison.OrdinalIgnoreCase) &&
                                              g.Node["Options"] is JsonArray options && options.Count < MaxMultiOptions);
        if (index >= 0)
        {
            var (label, address) = Append(mod, index, option, name);
            return (label, address, new ConvertedOptionPlacement(mod.Groups[index].Name, Json.GetString(option["Name"])!, false, false));
        }

        var groupName = ModGroupBuilder.UniqueName(mod, GroupName);
        ModGroupBuilder.Add(mod, groupName, GroupDescription, ModGroupBuilder.TopPriority(mod) + 1, "Multi", 1UL, [option]);
        return ($"{groupName} / {name}", new ContainerAddress(mod.Groups.Count - 1, 0),
            new ConvertedOptionPlacement(groupName, name, false, true));
    }

    /// <summary>Appends the option to a multi-select group under a name of its own there, switched on by default.</summary>
    private static (string Label, ContainerAddress Address) Append(PenumbraMod mod, int index, JsonObject option, string name)
    {
        var group = mod.Groups[index];
        var array = (JsonArray)group.Node["Options"]!;
        var taken = array.OfType<JsonObject>().Select(o => Json.GetString(o["Name"]) ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unique = name;
        for (var i = 2; taken.Contains(unique); i++) unique = $"{name} ({i})";
        option["Name"] = unique;
        array.Add(option);
        Json.TryGetULong(group.Node["DefaultSettings"], out var defaults);
        group.Node["DefaultSettings"] = defaults | 1UL << (array.Count - 1);
        // A group lists its containers when it is made, so the grown one is made again.
        mod.Groups[index] = new ModGroup(group.Node, index);
        return ($"{group.Name} / {unique}", new ContainerAddress(index, array.Count - 1));
    }
}
