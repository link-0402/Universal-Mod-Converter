namespace UniversalModConverter.Core;

/// <summary>Dispatches path edits through a parser for formats whose string tables can move.</summary>
public static class StructuredPathRewriter
{
    public static byte[] Rewrite(string filePath, byte[] input,
        IReadOnlyDictionary<string, string> replacements)
    {
        var extension = Path.GetExtension(filePath);
        // Material renames work for MDL v5 and v6 without the geometry parser.
        if (extension.Equals(".mdl", StringComparison.OrdinalIgnoreCase))
            return ResourceReferences.RewriteMdlStrings(input, replacements);
        if (extension.Equals(".mtrl", StringComparison.OrdinalIgnoreCase))
            return MtrlFile.RewritePaths(input, replacements);
        return BinaryPathRewriter.Rewrite(input, replacements);
    }
}
