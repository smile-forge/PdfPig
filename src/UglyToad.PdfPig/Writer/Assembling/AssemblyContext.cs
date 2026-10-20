namespace UglyToad.PdfPig.Writer.Assembling;

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Core;
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
    /// The output position of a source page, or null when it is no page of the source or is left out.
    /// </summary>
    public int? PositionOf(AssemblySource source, IndirectReference? page)
        => page.HasValue ? source.PageAt(page.Value)?.Slot?.Index : null;
}
