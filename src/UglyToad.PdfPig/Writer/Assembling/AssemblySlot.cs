namespace UglyToad.PdfPig.Writer.Assembling;

using Tokens;
using Core;

/// <summary>
/// A source page — or a part of it — where the assembly puts it: written as a page of its own, or placed on a sheet with
/// others. Its position and reference are its sheet's.
/// </summary>
internal sealed class AssemblySlot
{
    public AssemblySlot(
        AssemblySheet sheet,
        AssemblySource source,
        SourcePage page,
        AssembledContentTag? tag,
        PdfRectangle? region,
        PdfRectangle shown,
        TransformationMatrix? toSheet,
        IndirectReferenceToken? form)
    {
        Sheet = sheet;
        Source = source;
        Page = page;
        Tag = tag;
        Region = region;
        Shown = shown;
        ToSheet = toSheet;
        Form = form;
    }

    public AssemblySheet Sheet { get; }

    public int Index => Sheet.Index;

    public AssemblySource Source { get; }

    public SourcePage Page { get; }

    /// <summary>
    /// The reference of the page the slot is on: its own when it is written as itself, its sheet's when it is placed.
    /// </summary>
    public IndirectReferenceToken Reference => Sheet.Reference;

    public AssembledContentTag? Tag { get; }

    /// <summary>
    /// The part of the page shown, in its default user space; null for the whole page.
    /// </summary>
    public PdfRectangle? Region { get; }

    /// <summary>
    /// What of the page is shown, in its default user space: the region, or the whole page's crop box held to its media
    /// box.
    /// </summary>
    public PdfRectangle Shown { get; }

    /// <summary>
    /// From the page's default user space to its sheet's, for a page placed on a sheet; null for one written as itself,
    /// which keeps its own user space.
    /// </summary>
    public TransformationMatrix? ToSheet { get; }

    /// <summary>
    /// The form XObject holding the page's content that a placed page is drawn from — one for every placement of the
    /// page; null for a page written as itself.
    /// </summary>
    public IndirectReferenceToken? Form { get; }

    public bool IsPlaced => ToSheet is not null;

    /// <summary>
    /// Where the content the slot shows is drawn from: its form when it is placed, else the page it is written as.
    /// Placements of one page share it, and so the marked content in it.
    /// </summary>
    public IndirectReference Holder => (Form ?? Reference).Data;
}
