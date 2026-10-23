namespace UglyToad.PdfPig.Writer.Assembling;

using System.Collections.Generic;
using Core;
using Tokens;

/// <summary>
/// A page of the assembled document: its position and the reference it is written at, and what is on it — one source
/// page, or part of one, written as itself; or, on a sheet of its own size, the source pages placed on it.
/// </summary>
internal sealed class AssemblySheet
{
    private readonly List<AssemblySlot> slots = new();

    public AssemblySheet(int index, IndirectReferenceToken reference, PdfRectangle? bounds)
    {
        Index = index;
        Reference = reference;
        Bounds = bounds;
    }

    public int Index { get; }

    public IndirectReferenceToken Reference { get; }

    /// <summary>
    /// The sheet's media box when pages are placed on it; null for a page written as itself.
    /// </summary>
    public PdfRectangle? Bounds { get; }

    public bool IsImposed => Bounds is not null;

    /// <summary>
    /// What is on the sheet, in the order it is drawn: the one page a page written as itself is, or each placement.
    /// </summary>
    public IReadOnlyList<AssemblySlot> Slots => slots;

    public void Add(AssemblySlot slot) => slots.Add(slot);
}
