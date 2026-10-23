namespace UglyToad.PdfPig.Writer;

using System;
using System.Collections.Generic;
using System.Linq;
using Core;

/// <summary>
/// A page of a document being assembled: either a source page written as itself, or a sheet of a size of its own with
/// source pages placed on it — several printed to one sheet, or one page scaled to another size.
/// </summary>
public sealed class AssembledSheet
{
    private AssembledSheet(AssembledPage? page, PdfRectangle? bounds, IReadOnlyList<AssembledPlacement> placements)
    {
        Page = page;
        Bounds = bounds;
        Placements = placements;
    }

    /// <summary>
    /// Places <paramref name="placements"/>, in order, on a sheet <paramref name="width"/> by <paramref name="height"/>
    /// points; a sheet with nothing placed on it is a blank page. Each placed page's content is drawn clipped to the part
    /// of it shown; what lies on it — its annotations, article beads and tagged content, and the destinations into it —
    /// moves with it.
    /// </summary>
    public AssembledSheet(double width, double height, IReadOnlyList<AssembledPlacement> placements)
        : this(
            null,
            width > 0 && height > 0 ? new PdfRectangle(0, 0, width, height) : throw new ArgumentException("A sheet has an area."),
            placements?.ToArray() ?? throw new ArgumentNullException(nameof(placements)))
    {
    }

    /// <summary>
    /// The source page this sheet is, written as itself; null for a sheet with pages placed on it.
    /// </summary>
    public AssembledPage? Page { get; }

    /// <summary>
    /// The sheet's media box, from the origin; null for a page written as itself, which keeps its own boxes.
    /// </summary>
    public PdfRectangle? Bounds { get; }

    /// <summary>
    /// The pages placed on the sheet, drawn in order; empty for a page written as itself.
    /// </summary>
    public IReadOnlyList<AssembledPlacement> Placements { get; }

    /// <summary>
    /// <paramref name="page"/> written as itself, exactly as an assembly of pages writes it.
    /// </summary>
    public static AssembledSheet Of(AssembledPage page)
    {
        if (page.Document is null)
        {
            throw new ArgumentException("A sheet of one page is that page.", nameof(page));
        }

        return new AssembledSheet(page, null, []);
    }
}
