namespace UglyToad.PdfPig.Writer.Assembling;

using System.Collections.Generic;
using Core;
using Tokens;

/// <summary>
/// A page of a source document, and where it goes in the assembly: once, or once for each of its parts.
/// </summary>
internal sealed class SourcePage
{
    private readonly List<AssemblySlot> slots = new();

    public SourcePage(
        int number,
        IndirectReference reference,
        DictionaryToken dictionary,
        IReadOnlyList<DictionaryToken> ancestors,
        IReadOnlyList<IToken> annotations)
    {
        Number = number;
        Reference = reference;
        Dictionary = dictionary;
        Ancestors = ancestors;
        Annotations = annotations;
    }

    public int Number { get; }

    public IndirectReference Reference { get; }

    public DictionaryToken Dictionary { get; }

    /// <summary>
    /// The page tree nodes above the page, root first: where an inherited attribute it lacks comes from.
    /// </summary>
    public IReadOnlyList<DictionaryToken> Ancestors { get; }

    /// <summary>
    /// The page's annotations, as its <c>/Annots</c> array holds them.
    /// </summary>
    public IReadOnlyList<IToken> Annotations { get; }

    /// <summary>
    /// Where the page goes in the assembly, in output order: none when it is left out, more than one when it is split.
    /// </summary>
    public IReadOnlyList<AssemblySlot> Slots => slots;

    /// <summary>
    /// The first place the page goes, which what refers to the page lands on unless it lies in another part.
    /// </summary>
    public AssemblySlot? Slot => slots.Count > 0 ? slots[0] : null;

    public bool IsKept => slots.Count > 0;

    /// <summary>
    /// Whether the page is shown in more than one part, so what is on it goes to the part it lies in.
    /// </summary>
    public bool IsSplit => slots.Count > 1;

    public void Add(AssemblySlot slot) => slots.Add(slot);

    /// <summary>
    /// The part a point of the page lies in, in user space; an axis not given matches every part. The first part when
    /// none holds it.
    /// </summary>
    public AssemblySlot? SlotAt(double? x, double? y)
    {
        foreach (var slot in slots)
        {
            if (slot.Region is not { } region)
            {
                return slot;
            }

            var withinX = x is not { } px || px >= region.Left && px <= region.Right;
            var withinY = y is not { } py || py >= region.Bottom && py <= region.Top;
            if (withinX && withinY)
            {
                return slot;
            }
        }

        return Slot;
    }

    /// <summary>
    /// The part a rectangle of the page lies in: the one holding its centre.
    /// </summary>
    public AssemblySlot? SlotFor(PdfRectangle rectangle) => SlotAt(rectangle.Centroid.X, rectangle.Centroid.Y);
}
