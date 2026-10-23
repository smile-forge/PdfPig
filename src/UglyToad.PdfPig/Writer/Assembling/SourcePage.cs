namespace UglyToad.PdfPig.Writer.Assembling;

using System.Collections.Generic;
using Core;
using Tokens;

/// <summary>
/// A page of a source document, and where it goes in the assembly: once, once for each of its parts, or placed on
/// sheets.
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
    /// Where the page goes in the assembly, in output order: none when it is left out, more than one when it is split
    /// or placed more than once.
    /// </summary>
    public IReadOnlyList<AssemblySlot> Slots => slots;

    /// <summary>
    /// The first place the page goes, which what refers to the page lands on unless it lies in another part.
    /// </summary>
    public AssemblySlot? Slot => slots.Count > 0 ? slots[0] : null;

    public bool IsKept => slots.Count > 0;

    /// <summary>
    /// Whether the page is shown in more than one place — in parts, or placed again — so what is on it goes to the one
    /// it lies in.
    /// </summary>
    public bool IsSplit => slots.Count > 1;

    /// <summary>
    /// Whether the page is placed on a sheet anywhere, so what lies on it is moved to where it is placed.
    /// </summary>
    public bool IsPlaced => slots.Exists(slot => slot.IsPlaced);

    /// <summary>
    /// The form XObject the page's content is written as, for drawing it on sheets; null until it is placed on one.
    /// </summary>
    public IndirectReferenceToken? Form { get; private set; }

    public void Add(AssemblySlot slot) => slots.Add(slot);

    /// <summary>
    /// The form XObject every placement of the page draws, reserved the first time the page is placed.
    /// </summary>
    public IndirectReferenceToken FormFor(IPdfStreamWriter writer) => Form ??= writer.ReserveObjectNumber();

    /// <summary>
    /// The page's own entry for <paramref name="key"/>, or the one it inherits from the nearest node above it.
    /// </summary>
    public IToken? Inherited(NameToken key)
    {
        if (Dictionary.TryGet(key, out var own))
        {
            return own;
        }

        for (var i = Ancestors.Count - 1; i >= 0; i--)
        {
            if (Ancestors[i].TryGet(key, out var inherited))
            {
                return inherited;
            }
        }

        return null;
    }

    /// <summary>
    /// The part a point of the page lies in, in user space; an axis not given matches every part. The first part when
    /// none holds it.
    /// </summary>
    public AssemblySlot? SlotAt(double? x, double? y)
    {
        foreach (var slot in slots)
        {
            if (Holds(slot, x, y))
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

    /// <summary>
    /// Every place the page goes that shows a rectangle of it — that holds its centre — in output order: more than one
    /// when the page is placed more than once; the first place alone when none holds it.
    /// </summary>
    public IReadOnlyList<AssemblySlot> SlotsShowing(PdfRectangle rectangle)
    {
        var x = rectangle.Centroid.X;
        var y = rectangle.Centroid.Y;
        var showing = slots.FindAll(slot => Holds(slot, x, y));
        return showing.Count > 0 || Slot is null ? showing : [Slot];
    }

    private static bool Holds(AssemblySlot slot, double? x, double? y)
    {
        if (slot.Region is not { } region)
        {
            return true;
        }

        var withinX = x is not { } px || px >= region.Left && px <= region.Right;
        var withinY = y is not { } py || py >= region.Bottom && py <= region.Top;
        return withinX && withinY;
    }
}
