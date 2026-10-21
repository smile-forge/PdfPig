namespace UglyToad.PdfPig.Writer.Assembling;

using Tokens;
using Core;

/// <summary>
/// A page of the assembled document: its position, the source page it is — or the part of it — and the reference it
/// is written at.
/// </summary>
internal sealed class AssemblySlot
{
    public AssemblySlot(
        int index,
        AssemblySource source,
        SourcePage page,
        IndirectReferenceToken reference,
        AssembledContentTag? tag,
        PdfRectangle? region)
    {
        Index = index;
        Source = source;
        Page = page;
        Reference = reference;
        Tag = tag;
        Region = region;
    }

    public int Index { get; }

    public AssemblySource Source { get; }

    public SourcePage Page { get; }

    public IndirectReferenceToken Reference { get; }

    public AssembledContentTag? Tag { get; }

    /// <summary>
    /// The part of the page shown, in its default user space; null for the whole page.
    /// </summary>
    public PdfRectangle? Region { get; }
}
