namespace AdvancedPenumbraModConverter.Core;

public static class MetadataTransforms
{
    public static ushort RepositionEqdp(ushort entry, int sourceShift, int targetShift)
    {
        if (sourceShift is < 0 or > 14 || targetShift is < 0 or > 14 ||
            (sourceShift & 1) != 0 || (targetShift & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(sourceShift), "EQDP shifts must identify a two-bit field.");
        if (sourceShift == targetShift) return entry;
        const ushort bits = 0x3;
        var sourceMask = (ushort)(bits << sourceShift);
        var targetMask = (ushort)(bits << targetShift);
        var sourceBits = (ushort)((entry & sourceMask) >> sourceShift);
        return (ushort)((entry & ~(sourceMask | targetMask)) | (sourceBits << targetShift));
    }
}
