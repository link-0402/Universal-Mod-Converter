using System;
using System.Collections.Generic;
using AdvancedPenumbraItemConverter.Core;
using FFXIVClientStructs.Havok.Animation;
using FFXIVClientStructs.Havok.Common.Base.Object;
using FFXIVClientStructs.Havok.Common.Serialize.Resource;
using FFXIVClientStructs.Havok.Common.Serialize.Util;
using Dalamud.Plugin.Services;

namespace AdvancedPenumbraItemConverter.Services;

/// <summary>
/// Thin game-runtime adapter. Callers invoke this only from the framework/UI thread
/// during preview and keep the returned hierarchy as ordinary managed data.
/// </summary>
internal sealed unsafe class HavokSkeletonHierarchyReader(IFramework framework) : ISkeletonHierarchyReader
{
    public BoneHierarchy Read(ReadOnlyMemory<byte> sklbBytes)
    {
        var ownedBytes = sklbBytes.ToArray();
        return framework.IsInFrameworkUpdateThread
            ? ReadOnFrameworkThread(ownedBytes)
            : framework.RunOnFrameworkThread(() => ReadOnFrameworkThread(ownedBytes)).GetAwaiter().GetResult();
    }

    private static BoneHierarchy ReadOnFrameworkThread(ReadOnlyMemory<byte> sklbBytes)
    {
        var hkx = SklbEnvelope.ExtractHavok(sklbBytes.Span);
        fixed (byte* buffer = hkx)
        {
            hkSerializeUtil.ErrorDetails error = default;
            hkSerializeUtil.LoadOptions options = default;
            var resource = hkSerializeUtil.LoadFromBuffer(buffer, hkx.Length, &error, &options);
            if (resource == null)
                throw new InvalidDataException($"Havok rejected the SKLB payload (error {error.Id.Value}).");

            try
            {
                var root = (hkRootLevelContainer*)resource->GetContentsPointer("hkRootLevelContainer", null);
                if (root == null)
                    throw new InvalidDataException("SKLB contains no hkRootLevelContainer.");
                var container = (hkaAnimationContainer*)root->findObjectByType("hkaAnimationContainer", null);
                if (container == null || container->Skeletons.Length <= 0 || container->Skeletons.Data == null)
                    throw new InvalidDataException("SKLB contains no animation skeleton.");

                var skeleton = container->Skeletons[0].ptr;
                if (skeleton == null || skeleton->Bones.Length <= 0 || skeleton->Bones.Length > 16_384 ||
                    skeleton->Bones.Data == null || skeleton->ParentIndices.Data == null ||
                    skeleton->ParentIndices.Length != skeleton->Bones.Length)
                    throw new InvalidDataException("SKLB skeleton bone arrays are invalid.");

                var names = new string[skeleton->Bones.Length];
                for (var index = 0; index < names.Length; index++)
                    names[index] = skeleton->Bones[index].Name.String
                        ?? throw new InvalidDataException($"SKLB bone {index} has no name.");

                var parents = new Dictionary<string, string?>(names.Length, StringComparer.Ordinal);
                for (var index = 0; index < names.Length; index++)
                {
                    var parentIndex = skeleton->ParentIndices[index];
                    if (parentIndex < -1 || parentIndex >= names.Length)
                        throw new InvalidDataException($"SKLB bone '{names[index]}' has invalid parent {parentIndex}.");
                    if (!parents.TryAdd(names[index], parentIndex < 0 ? null : names[parentIndex]))
                        throw new InvalidDataException($"SKLB contains duplicate bone '{names[index]}'.");
                }
                return new BoneHierarchy(parents);
            }
            finally
            {
                ((hkReferencedObject*)resource)->RemoveReference();
            }
        }
    }
}
