namespace UglyToad.PdfPig.Writer.Assembling;

using Tokens;

/// <summary>
/// A page of the assembled document: its position, the source page it is, and the reference it is written at.
/// </summary>
internal sealed class AssemblySlot
{
    public AssemblySlot(int index, AssemblySource source, SourcePage page, IndirectReferenceToken reference, AssembledContentTag? tag)
    {
        Index = index;
        Source = source;
        Page = page;
        Reference = reference;
        Tag = tag;
    }

    public int Index { get; }

    public AssemblySource Source { get; }

    public SourcePage Page { get; }

    public IndirectReferenceToken Reference { get; }

    public AssembledContentTag? Tag { get; }
}
