using System;
using System.Collections.Generic;
using System.IO;
using UniversalModConverter.Core;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.Havok.Animation.Animation;
using FFXIVClientStructs.Havok.Common.Base.Types;

namespace UniversalModConverter.Services.Animations;

/// <summary>
/// Copies facial animation bindings from one .pap's Havok container into another's, using the
/// game's own Havok runtime to load and save both. Nothing is resampled: the donor's animations
/// are carried over as they are, because a face plays on the face skeleton, which does not
/// change with the body it is attached to.
/// </summary>
internal sealed unsafe class HavokExpressionMerger(IFramework framework) : IExpressionMerger
{
    /// <summary>Loading and saving containers needs only the type registry, which every build has.</summary>
    public string? UnavailableReason => null;

    public (byte[] Havok, int OriginalBindings) Append(byte[] target, byte[] donor, IReadOnlyList<int> donorBindings)
    {
        if (framework.IsInFrameworkUpdateThread)
            throw new InvalidOperationException("Expressions cannot be attached on the framework thread.");
        return framework.RunOnFrameworkThread(() => AppendNow(target, donor, donorBindings)).GetAwaiter().GetResult();
    }

    private static (byte[] Havok, int OriginalBindings) AppendNow(byte[] target, byte[] donor, IReadOnlyList<int> donorBindings)
    {
        if (donorBindings.Count == 0) throw new InvalidDataException("No facial animation to attach.");

        // Disposal runs in reverse: the target goes first, then the donor it points into, then
        // the arena both may reference. Havok follows the copied bindings into the donor while
        // saving, so the donor must outlive the save.
        using var arena = new HavokAnimation.Arena();
        using var donorDocument = new HavokAnimation.Document(donor);
        using var targetDocument = new HavokAnimation.Document(target);

        var destination = targetDocument.Container;
        var source = donorDocument.Container;
        var original = destination->Bindings.Length;
        if (destination->Animations.Length != original)
            throw new InvalidDataException("The animation's binding and animation lists differ in length, " +
                                           "so new animations cannot be added safely.");
        foreach (var index in donorBindings)
            if (index < 0 || index >= source->Bindings.Length || source->Bindings[index].ptr == null ||
                source->Bindings[index].ptr->Animation.ptr == null)
                throw new InvalidDataException("The expression's facial animation refers to a binding it does not have.");

        var oldBindings = destination->Bindings;
        var oldAnimations = destination->Animations;
        try
        {
            // Arena arrays carry Havok's don't-deallocate flag, and the binding copies have
            // reference counting switched off, so Havok never frees plugin memory.
            var bindings = arena.Array<hkRefPtr<hkaAnimationBinding>>(original + donorBindings.Count);
            var animations = arena.Array<hkRefPtr<hkaAnimation>>(original + donorBindings.Count);
            for (var i = 0; i < original; i++)
            {
                bindings.Data[i] = oldBindings.Data[i];
                animations.Data[i] = oldAnimations.Data[i];
            }

            for (var k = 0; k < donorBindings.Count; k++)
            {
                var copy = arena.CopyBinding(source->Bindings[donorBindings[k]].ptr);
                bindings.Data[original + k] = new hkRefPtr<hkaAnimationBinding> { ptr = copy };
                animations.Data[original + k] = new hkRefPtr<hkaAnimation> { ptr = copy->Animation.ptr };
            }

            destination->Bindings = bindings;
            destination->Animations = animations;
            var saved = targetDocument.Save();

            using var check = new HavokAnimation.Document(saved);
            if (check.Container->Bindings.Length != original + donorBindings.Count ||
                check.Container->Animations.Length != original + donorBindings.Count)
                throw new InvalidDataException("Saving the merged animation lost an animation.");
            for (var i = 0; i < original; i++)
                if (HavokAnimation.Fingerprint(check.Container->Bindings[i].ptr) !=
                    HavokAnimation.Fingerprint(oldBindings.Data[i].ptr))
                    throw new InvalidDataException("Attaching the expression changed an existing animation.");
            return (saved, original);
        }
        finally
        {
            // Put the container back before Havok tears it down, so it never sees our arrays.
            destination->Bindings = oldBindings;
            destination->Animations = oldAnimations;
        }
    }
}
