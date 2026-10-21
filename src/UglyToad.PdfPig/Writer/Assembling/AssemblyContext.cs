namespace UglyToad.PdfPig.Writer.Assembling;

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Core;
using Parser.Parts;
using Tokenization.Scanner;
using Tokens;

/// <summary>
/// What every part of an assembly shares: the writer, the sources and the pages planned from them, the objects being
/// rewritten, and copying a source's tokens into the output through that source's map.
/// </summary>
internal sealed class AssemblyContext
{
    public AssemblyContext(
        IPdfStreamWriter writer,
        IReadOnlyList<AssemblySource> sources,
        IReadOnlyList<AssemblySlot> slots,
        CancellationToken cancellation)
    {
        Writer = writer;
        Sources = sources;
        Slots = slots;
        Cancellation = cancellation;
        NullReference = writer.WriteToken(NullToken.Instance);
        Rewrites = new RewrittenObjects(this);
        Destinations = new DestinationResolver();
    }

    public IPdfStreamWriter Writer { get; }

    /// <summary>
    /// The sources, the primary first.
    /// </summary>
    public IReadOnlyList<AssemblySource> Sources { get; }

    public AssemblySource Primary => Sources[0];

    /// <summary>
    /// The pages of the output, in order.
    /// </summary>
    public IReadOnlyList<AssemblySlot> Slots { get; }

    public CancellationToken Cancellation { get; }

    /// <summary>
    /// The one null object every reference to something left out is pointed at.
    /// </summary>
    public IndirectReferenceToken NullReference { get; }

    public RewrittenObjects Rewrites { get; }

    public DestinationResolver Destinations { get; }

    public void ThrowIfCancelled() => Cancellation.ThrowIfCancellationRequested();

    /// <summary>
    /// <paramref name="token"/> in the output's terms: each reference it holds is the object written for it.
    /// </summary>
    public IToken Copy(AssemblySource source, IToken token)
        => WriterUtil.CopyToken(Writer, token, source.Scanner, source.References);

    /// <summary>
    /// Whether a copied token is nothing: null, or a reference to something left out.
    /// </summary>
    public bool IsNull(IToken? token)
        => token is null || token is NullToken || token is IndirectReferenceToken reference && reference.Data.Equals(NullReference.Data);

    /// <summary>
    /// Whether the annotation is written: it was not left out, and it is on a page kept or on no page at all.
    /// </summary>
    public bool KeepsAnnotation(AssemblySource source, IndirectReference annotation)
    {
        if (Rewrites.IsDropped(source, annotation))
        {
            return false;
        }

        var holding = source.PagesHolding(annotation);
        return holding.Count == 0 || holding.Any(page => page.IsKept);
    }

    /// <summary>
    /// Whether <paramref name="page"/> is a page of the source that is left out — what a destination pointing at it,
    /// and anything that lived on it, goes with.
    /// </summary>
    public bool IsPageLeftOut(AssemblySource source, IndirectReference? page)
        => page.HasValue && source.PageAt(page.Value) is { IsKept: false };

    /// <summary>
    /// The output position of a source page — its first part's — or null when it is no page of the source or is left out.
    /// </summary>
    public int? PositionOf(AssemblySource source, IndirectReference? page)
        => page.HasValue ? source.PageAt(page.Value)?.Slot?.Index : null;

    /// <summary>
    /// Where a destination goes in the output: the part of its page its point lies in; null when it names no page kept.
    /// </summary>
    public AssemblySlot? SlotOf(AssemblySource source, ResolvedDestination target)
    {
        if (target.Page is not { } page || source.PageAt(page) is not { IsKept: true } kept)
        {
            return null;
        }

        if (!kept.IsSplit || target.Explicit is not { } destination)
        {
            return kept.Slot;
        }

        var (x, y) = PointOf(destination, source.Scanner);
        return kept.SlotAt(x, y);
    }

    /// <summary>
    /// <paramref name="destination"/> as the source writes it, pointed at the part of a split page its point lies in: the
    /// page in its explicit destination becomes that part — and a named one becomes explicit, since a name can only
    /// stand for one of the parts. Any other destination comes back as it is.
    /// </summary>
    public IToken Retargeted(AssemblySource source, IToken destination, ResolvedDestination target)
    {
        if (target.Page is not { } page
            || source.PageAt(page) is not { IsSplit: true }
            || target.Explicit is not { } array
            || SlotOf(source, target) is not { } slot)
        {
            return destination;
        }

        var items = array.Data.ToList();
        items[0] = Rewrites.Placeholder(source, slot.Reference);
        var retargeted = new ArrayToken(items);

        return source.Resolve<DictionaryToken>(destination) is { } holder && holder.ContainsKey(NameToken.D)
            ? holder.With(NameToken.D, retargeted)
            : retargeted;
    }

    /// <summary>
    /// Puts an object that lies on a split page — an annotation, an article bead — on the part it lies in.
    /// </summary>
    public void PlaceOn(AssemblySource source, IndirectReference reference, AssemblySlot slot) => placed[(source, reference)] = slot;

    /// <summary>
    /// The part of a split page an object was put on; null when it was put on none.
    /// </summary>
    public AssemblySlot? PlacedOn(AssemblySource source, IndirectReference reference)
        => placed.TryGetValue((source, reference), out var slot) ? slot : null;

    /// <summary>
    /// Whether an entry of a page's <c>/Annots</c> or <c>/B</c> belongs on <paramref name="slot"/>: every entry does,
    /// unless the page is split and the entry was put on another of its parts.
    /// </summary>
    public bool IsOn(AssemblySlot slot, IToken entry)
        => !slot.Page.IsSplit
           || entry is not IndirectReferenceToken reference
           || PlacedOn(slot.Source, reference.Data) is not { } on
           || on == slot;

    /// <summary>
    /// The point of the page an explicit destination names, in user space; an axis it does not name is null.
    /// </summary>
    private static (double? X, double? Y) PointOf(ArrayToken destination, IPdfTokenScanner scanner)
    {
        double? At(int index) =>
            index < destination.Length && DirectObjectFinder.TryGet(destination.Data[index], scanner, out NumericToken? number)
                ? number.Double
                : null;

        double? Middle(double? first, double? second) => first is { } a && second is { } b ? (a + b) / 2 : first ?? second;

        var kind = destination.Length > 1 ? AssemblyTokens.Name(destination.Data[1], scanner)?.Data : null;
        return kind switch
        {
            "XYZ"             => (At(2), At(3)),
            "FitH" or "FitBH" => (null, At(2)),
            "FitV" or "FitBV" => (At(2), null),
            "FitR"            => (Middle(At(2), At(4)), Middle(At(3), At(5))),
            _                 => (null, null),
        };
    }

    private readonly Dictionary<(AssemblySource, IndirectReference), AssemblySlot> placed = new();
}
