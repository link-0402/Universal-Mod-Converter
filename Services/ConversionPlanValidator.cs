using System;
using System.IO;
using System.Linq;
using AdvancedPenumbraItemConverter.Core;
using AdvancedPenumbraItemConverter.Models;

namespace AdvancedPenumbraItemConverter.Services;

internal static class ConversionPlanValidator
{
    public static void Finalize(ConversionTask task)
    {
        var root = Path.GetFullPath(task.ModDirectory);
        var sources = task.PlannedRenames.Select(r => Path.GetFullPath(r.OldPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var destinations = task.PlannedRenames.Select(r => Path.GetFullPath(r.NewPath))
            .Concat(task.PlannedGeneratedFiles.Select(f => Path.GetFullPath(f.FilePath))).ToArray();

        try
        {
            foreach (var path in sources) PathSafety.EnsureContained(root, path, true);
            foreach (var path in destinations) PathSafety.EnsureContained(root, path);
            foreach (var json in task.PlannedJsonChanges) PathSafety.EnsureContained(root, json.FilePath, true);
            foreach (var binary in task.PlannedBinaryPatches) PathSafety.EnsureContained(root, binary.FilePath, true);
            foreach (var mdl in task.PlannedMdlChanges) PathSafety.EnsureContained(root, mdl.FilePath, true);
            PathSafety.ValidateNoCaseCollisions(destinations);

            foreach (var destination in destinations)
                if ((File.Exists(destination) || Directory.Exists(destination)) && !sources.Contains(destination))
                    task.Diagnostics.Add(new PlanDiagnostic("destination_collision",
                        $"Destination already exists: {Path.GetRelativePath(root, destination)}", true));
        }
        catch (Exception ex)
        {
            task.Diagnostics.Add(new PlanDiagnostic("unsafe_path", ex.Message, true));
        }

        var inputFiles = task.AllAssetFiles
            .Concat(task.PlannedJsonChanges.Select(j => j.FilePath))
            .Concat(task.PlannedBinaryPatches.Select(b => b.FilePath))
            .Concat(task.PlannedMdlChanges.Select(m => m.FilePath))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        task.SourceFingerprint = ModFingerprint.Compute(root, inputFiles);

        var operations = task.PlannedRenames.Select(r => new ConversionOperation("rename", r.OldPath, r.NewPath))
            .Concat(task.PlannedGeneratedFiles.Select(f => new ConversionOperation("dependency", f.FilePath,
                MdlRaceConverter.Hash(f.Data.AsSpan()))))
            .Concat(task.PlannedJsonChanges.Select(j => new ConversionOperation("metadata", j.FilePath, j.FilePath)))
            .Concat(task.PlannedBinaryPatches.Select(b => new ConversionOperation("binary", b.FilePath, b.FilePath)))
            .Concat(task.PlannedMdlChanges.Select(m => new ConversionOperation("mdl", m.FilePath, m.OutputHash)))
            .ToArray();
        task.PlanFingerprint = ModFingerprint.ComputePlan(operations);
        if (operations.Length == 0)
            task.Diagnostics.Add(new PlanDiagnostic("empty_plan", "No connected conversion operations were found.", true));

        task.IsPlanned = true;
        task.ErrorMessage = task.HasBlockers
            ? string.Join(" ", task.Diagnostics.Where(d => d.IsBlocker).Select(d => d.Message))
            : null;
    }

    public static string RecomputeSourceFingerprint(ConversionTask task)
    {
        var inputs = task.AllAssetFiles
            .Concat(task.PlannedJsonChanges.Select(j => j.FilePath))
            .Concat(task.PlannedBinaryPatches.Select(b => b.FilePath))
            .Concat(task.PlannedMdlChanges.Select(m => m.FilePath))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return ModFingerprint.Compute(task.ModDirectory, inputs);
    }
}
