namespace AdvancedPenumbraItemConverter.Core;

/// <summary>Writes a <see cref="GearConversionPlan"/> to disk.</summary>
public static class GearConversionExecutor
{
    /// <summary>Creates the converted mod in <paramref name="outputDirectory"/> (which must be empty or absent).</summary>
    public static void WriteNewMod(GearConversionPlan plan, string sourceDirectory, string outputDirectory,
        string displayName, Action<string>? log = null)
    {
        if (plan.HasBlockers) throw new InvalidOperationException("The conversion plan has blockers.");
        Directory.CreateDirectory(outputDirectory);
        if (Directory.EnumerateFileSystemEntries(outputDirectory).Any())
            throw new IOException($"The output directory is not empty: {outputDirectory}");

        foreach (var operation in plan.Files)
        {
            var destination = PathSafety.ResolveRelative(outputDirectory, operation.Destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            switch (operation.Operation)
            {
                case LocalFileOperation.Copy:
                    File.Copy(PathSafety.ResolveRelative(sourceDirectory, operation.Source!), destination, overwrite: false);
                    break;
                case LocalFileOperation.Write:
                    using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
                        stream.Write(operation.Content);
                    break;
                default:
                    throw new InvalidOperationException($"{operation.Operation} is not valid when creating a new mod.");
            }
        }

        plan.Result.Meta["Name"] = displayName;
        plan.Result.Save(outputDirectory);
        log?.Invoke($"Wrote {plan.Files.Count} file(s) and the {plan.Result.Format} mod definition.");
    }

    /// <summary>Applies the plan to <paramref name="modDirectory"/> (normally a staged copy of the mod).</summary>
    public static void ApplyInPlace(GearConversionPlan plan, string modDirectory, Action<string>? log = null)
    {
        if (plan.HasBlockers) throw new InvalidOperationException("The conversion plan has blockers.");
        var vacated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in plan.Files)
        {
            var destination = PathSafety.ResolveRelative(modDirectory, operation.Destination);
            switch (operation.Operation)
            {
                case LocalFileOperation.Move:
                    var source = PathSafety.ResolveRelative(modDirectory, operation.Source!);
                    if (File.Exists(destination)) throw new IOException($"Planned destination already exists: {operation.Destination}");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Move(source, destination);
                    vacated.Add(Path.GetDirectoryName(source)!);
                    log?.Invoke($"Moved {operation.Source} → {operation.Destination}");
                    break;
                case LocalFileOperation.Write:
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.WriteAllBytes(destination, operation.Content!);
                    log?.Invoke($"Wrote {operation.Destination} ({operation.Reason})");
                    break;
                case LocalFileOperation.Delete:
                    File.Delete(destination);
                    vacated.Add(Path.GetDirectoryName(destination)!);
                    break;
                default:
                    throw new InvalidOperationException($"{operation.Operation} is not valid for an in-place conversion.");
            }
        }

        plan.Result.Save(modDirectory);
        var root = Path.GetFullPath(modDirectory).TrimEnd(Path.DirectorySeparatorChar);
        foreach (var directory in vacated.OrderByDescending(d => d.Length))
        {
            var current = directory;
            while (current.Length > root.Length && Directory.Exists(current) &&
                   !Directory.EnumerateFileSystemEntries(current).Any())
            {
                Directory.Delete(current);
                current = Path.GetDirectoryName(current)!;
            }
        }
    }
}

public sealed record VerificationIssue(bool IsError, string Message);

/// <summary>
/// Checks a converted mod the way the game would load the target item: every target model's
/// materials must resolve for every IMC material folder in use, and every material's
/// textures must exist in the mod or the game.
/// </summary>
public static class GearConversionVerifier
{
    public static List<VerificationIssue> Verify(string modDirectory, GearItem target, GearItem? source,
        IGameFileProvider game)
    {
        var issues = new List<VerificationIssue>();
        var mod = PenumbraMod.Load(modDirectory);
        var files = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var swaps = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var container in mod.Containers)
        {
            foreach (var (key, local) in container.FileEntries())
            {
                var path = GamePath.Normalize(key);
                if (!files.TryGetValue(path, out var list)) files[path] = list = [];
                list.Add(local);
            }
            foreach (var (key, value) in container.SwapEntries()) swaps.TryAdd(GamePath.Normalize(key), GamePath.Normalize(value));
        }

        bool Exists(string path) => files.ContainsKey(path) || swaps.ContainsKey(path) || game.FileExists(path);

        IEnumerable<byte[]> Contents(string path)
        {
            if (files.TryGetValue(path, out var locals))
                foreach (var local in locals)
                {
                    var full = PathSafety.ResolveRelative(modDirectory, GamePath.ToLocal(local));
                    if (File.Exists(full)) yield return File.ReadAllBytes(full);
                    else issues.Add(new VerificationIssue(true, $"{local} (for {path}) does not exist."));
                }
            else if (swaps.TryGetValue(path, out var swapped) && game.ReadFile(swapped) is { } swappedBytes)
                yield return swappedBytes;
            else if (game.ReadFile(path) is { } vanilla)
                yield return vanilla;
        }

        // Material folders the target's variants resolve to.
        var folders = new SortedSet<int>();
        var imc = game.ReadFile(GearSlots.ImcFile(target)) is { } imcBytes
            ? GameMetadata.ReadImc(imcBytes, target.Slot.ImcPartIndex()).ToDictionary(r => r.Variant, r => r.Entry)
            : [];
        foreach (var manipulation in mod.Default.Manipulations?.OfType<System.Text.Json.Nodes.JsonObject>() ?? [])
            if (GearManipulations.IsImcFor(manipulation, target, null) &&
                ImcEntry.FromJson(manipulation["Manipulation"]?["Entry"]) is { } entry)
                imc[(ushort)Json.GetInt(manipulation["Manipulation"]!["Variant"], 0)] = entry;
        folders.UnionWith(imc.Values.Select(e => (int)e.MaterialId).Where(m => m > 0));
        if (folders.Count == 0) folders.Add(1);

        var checkedMaterials = new HashSet<string>(StringComparer.Ordinal);
        var models = 0;
        foreach (var race in GenderRaces.Playable)
        {
            var model = GamePath.Normalize(GearSlots.ModelPath(target, race));
            if (!files.ContainsKey(model) && !swaps.ContainsKey(model)) continue;
            models++;
            foreach (var bytes in Contents(model))
            foreach (var material in SafeRead(model, bytes, issues))
            {
                if (ResourceReferences.IsSkinMaterial(material)) continue;
                var candidates = material.TrimStart('/').Contains('/')
                    ? [GamePath.Normalize(material)]
                    : folders.Select(f => $"{target.Root}/material/v{f:D4}/{GamePath.Normalize(material)}");
                foreach (var path in candidates)
                {
                    if (!checkedMaterials.Add(path)) continue;
                    if (!Exists(path))
                    {
                        issues.Add(new VerificationIssue(true, $"{model} needs {path}, which exists neither in the mod nor in the game."));
                        continue;
                    }

                    foreach (var materialBytes in Contents(path))
                    foreach (var texture in SafeRead(path, materialBytes, issues))
                        if (!Exists(GamePath.Normalize(texture)))
                            issues.Add(new VerificationIssue(true, $"{path} needs {texture}, which exists neither in the mod nor in the game."));
                }
            }
        }

        if (models == 0)
            issues.Add(new VerificationIssue(true, $"The mod provides no model for {target.Root} ({target.Slot})."));

        if (source is { } s)
        {
            var token = $"{s.PathEndpoint.Token}_{s.Slot.Suffix()}";
            foreach (var path in files.Keys.Concat(swaps.Keys)
                         .Where(p => p.StartsWith(s.Root + "/", StringComparison.Ordinal) && Path.GetFileName(p).Contains(token)))
                issues.Add(new VerificationIssue(false, $"The mod still redirects {path}."));
        }

        return issues;
    }

    private static IReadOnlyList<string> SafeRead(string path, byte[] bytes, List<VerificationIssue> issues)
    {
        try { return ResourceReferences.Read(path, bytes); }
        catch (Exception ex)
        {
            issues.Add(new VerificationIssue(true, $"{path} cannot be parsed: {ex.Message}"));
            return [];
        }
    }
}
