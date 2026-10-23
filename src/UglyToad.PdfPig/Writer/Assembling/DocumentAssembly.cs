namespace UglyToad.PdfPig.Writer.Assembling;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Content;
using Core;
using Geometry;
using Graphics;
using Logging;
using Tokens;

/// <summary>
/// One assembly, start to end: the sources and the pages planned from them, what is left out, each carrier prepared,
/// the pages written, each carrier completed, the claimed objects written, and the catalog and trailer.
/// </summary>
internal sealed class DocumentAssembly
{
    private readonly IReadOnlyList<AssembledSheet> sheets;
    private readonly Stream output;
    private readonly AssemblyOptions options;
    private readonly CancellationToken cancellation;

    public DocumentAssembly(IReadOnlyList<AssembledSheet> sheets, Stream output, AssemblyOptions options, CancellationToken cancellation)
    {
        this.sheets = sheets;
        this.output = output;
        this.options = options;
        this.cancellation = cancellation;
    }

    public void Write()
    {
        var sources = Sources(out var byDocument);
        foreach (var source in sources)
        {
            Refuse(source);
        }

        var version = Math.Max(options.Version ?? 1.0, sources.Max(source => source.Document.Version));

        using var writer = new PdfStreamWriter(output, disposeStream: false);
        writer.InitializePdf(version);

        var (planned, slots) = Plan(writer, byDocument);
        var context = new AssemblyContext(writer, sources, planned, slots, cancellation);
        LeaveOut(context);

        var annotations = new AnnotationCarrier();
        var structure = new StructureCarrier(annotations);
        var carriers = new IAssemblyCarrier[]
        {
            annotations,
            new AcroFormCarrier(),
            new ThreadCarrier(),
            structure,
            new OutlineCarrier(),
            new NameTreeCarrier(),
            new PageLabelCarrier(),
            new CatalogCarrier(),
        };

        foreach (var carrier in carriers)
        {
            context.ThrowIfCancelled();
            carrier.Prepare(context);
        }

        var catalog = new Dictionary<NameToken, IToken>
        {
            [NameToken.Type] = NameToken.Catalog,
            [NameToken.Pages] = new PageTreeWriter(context, annotations, structure).Write(),
        };

        foreach (var carrier in carriers)
        {
            context.ThrowIfCancelled();
            carrier.Complete(context, catalog);
        }

        context.Rewrites.WriteAll();

        var catalogReference = writer.WriteToken(new DictionaryToken(catalog));
        writer.CompletePdf(catalogReference, Information(context));
    }

    /// <summary>
    /// The pages each sheet takes, in order: the one page a page written as itself is, else each one placed on it.
    /// </summary>
    private static IEnumerable<AssembledPage> PagesOf(AssembledSheet sheet)
        => sheet.Page is { } page ? [page] : sheet.Placements.Select(placement => placement.Page);

    private List<AssemblySource> Sources(out Dictionary<PdfDocument, AssemblySource> byDocument)
    {
        var primary = options.Primary
                      ?? sheets.SelectMany(PagesOf).Select(page => page.Document).FirstOrDefault()
                      ?? throw new ArgumentException("An assembly of blank sheets is built on a primary document, and none was given.");
        var sources = new List<AssemblySource> { new(primary, 0) };
        byDocument = new Dictionary<PdfDocument, AssemblySource>(IdentityComparer<PdfDocument>.Instance) { [primary] = sources[0] };

        foreach (var page in sheets.SelectMany(PagesOf))
        {
            if (!byDocument.ContainsKey(page.Document))
            {
                var source = new AssemblySource(page.Document, sources.Count);
                sources.Add(source);
                byDocument[page.Document] = source;
            }
        }

        return sources;
    }

    private static void Refuse(AssemblySource source)
    {
        if (source.Dictionary(source.Catalog, NameToken.AcroForm) is { } form && form.ContainsKey(NameToken.Xfa))
        {
            throw new AssemblyRefusedException(
                source.Document,
                AssemblyRefusal.XfaForm,
                "The document has an XFA form, which lays out the whole document itself and cannot follow its pages.");
        }
    }

    private (List<AssemblySheet> Sheets, List<AssemblySlot> Slots) Plan(IPdfStreamWriter writer, Dictionary<PdfDocument, AssemblySource> byDocument)
    {
        var planned = new List<AssemblySheet>(sheets.Count);
        var slots = new List<AssemblySlot>(sheets.Count);
        var lastNumber = new Dictionary<AssemblySource, int>();

        void Put(AssemblySheet sheet, AssembledPage assembled, TransformationMatrix? place)
        {
            var source = byDocument[assembled.Document];
            var page = source.Page(assembled.PageNumber);

            if (place is null && page.Slots.Any(placed => !placed.IsPlaced && Equals(placed.Region, assembled.Region)))
            {
                throw new ArgumentException(
                    $"Page {assembled.PageNumber} of a document is assembled more than once; a page, or a part of one, may appear only once as a page of its own.",
                    nameof(sheets));
            }

            var shown = assembled.Region ?? Shown(source, page);
            var slot = new AssemblySlot(
                sheet,
                source,
                page,
                assembled.Tag,
                assembled.Region,
                shown,
                place is { } onSheet ? ToShown(source, page, shown).Multiply(onSheet) : null,
                place is null ? null : page.FormFor(writer));

            if (!page.IsKept)
            {
                // What refers to the page lands on its first part, unless it lies in another.
                source.References.Add(page.Reference, slot.Reference);
            }

            page.Add(slot);
            sheet.Add(slot);
            slots.Add(slot);

            if (lastNumber.TryGetValue(source, out var last) && page.Number < last)
            {
                source.PreservesOrder = false;
            }

            lastNumber[source] = page.Number;
        }

        for (var i = 0; i < sheets.Count; i++)
        {
            var assembled = sheets[i];
            var sheet = new AssemblySheet(i, writer.ReserveObjectNumber(), assembled.Bounds);
            if (assembled.Page is { } page)
            {
                Put(sheet, page, null);
            }
            else
            {
                foreach (var placement in assembled.Placements)
                {
                    Put(sheet, placement.Page, placement.Place);
                }
            }

            planned.Add(sheet);
        }

        return (planned, slots);
    }

    /// <summary>
    /// What a page shows, in its default user space: its crop box held to its media box, as a reader shows it.
    /// </summary>
    private static PdfRectangle Shown(AssemblySource source, SourcePage page)
    {
        var media = Box(source, page, NameToken.MediaBox) ?? new PdfRectangle(0, 0, 612, 792);
        return Box(source, page, NameToken.CropBox) is { } crop && crop.Intersect(media) is { } held ? held : media;
    }

    private static PdfRectangle? Box(AssemblySource source, SourcePage page, NameToken name)
    {
        if (source.Resolve<ArrayToken>(page.Inherited(name)) is not { Length: 4 } array)
        {
            return null;
        }

        var n = array.Data.Select(item => source.Resolve<NumericToken>(item)?.Double ?? 0).ToArray();
        var box = new PdfRectangle(Math.Min(n[0], n[2]), Math.Min(n[1], n[3]), Math.Max(n[0], n[2]), Math.Max(n[1], n[3]));
        return box.Width > 0 && box.Height > 0 ? box : null;
    }

    /// <summary>
    /// From a page's default user space to the page as a reader shows <paramref name="shown"/> of it: turned by its
    /// rotation, its bottom left at the origin, in points however large its user space unit.
    /// </summary>
    private static TransformationMatrix ToShown(AssemblySource source, SourcePage page, PdfRectangle shown)
    {
        var turn = AssemblyTokens.Integer(page.Inherited(NameToken.Rotate), source.Scanner) ?? 0;
        turn = (turn % 360 + 360) % 360;
        var rotation = new PageRotationDegrees(turn % 90 == 0 ? turn : 0);
        var initial = OperationContextHelper.GetInitialMatrix(UserSpaceUnit.Default, new CropBox(shown), rotation, new NoOpLog());

        var unit = source.Resolve<NumericToken>(page.Dictionary.TryGet(NameToken.UserUnit, out var u) ? u : null)?.Double ?? 1;
        return unit > 0 && unit != 1 ? initial.Multiply(TransformationMatrix.GetScaleMatrix(unit, unit)) : initial;
    }

    /// <summary>
    /// Points every page tree node at the null object — the tree is written anew — along with the annotations and
    /// article beads of each page left out, which go with their page; and each source's catalog, which only the
    /// carriers read from, so an object that refers back to it cannot copy a whole source document.
    /// </summary>
    private static void LeaveOut(AssemblyContext context)
    {
        foreach (var source in context.Sources)
        {
            context.Rewrites.Drop(source, source.Document.Structure.Trailer.Root);

            foreach (var node in source.TreeNodes)
            {
                if (!source.References.ContainsKey(node))
                {
                    context.Rewrites.Drop(source, node);
                }
            }

            foreach (var page in source.Pages)
            {
                if (page.IsKept)
                {
                    continue;
                }

                foreach (var annotation in page.Annotations)
                {
                    if (annotation is IndirectReferenceToken reference && !source.PagesHolding(reference.Data).Any(holder => holder.IsKept))
                    {
                        context.Rewrites.Drop(source, reference.Data);
                    }
                }

                foreach (var bead in source.Array(page.Dictionary, NameToken.B)?.Data ?? [])
                {
                    if (bead is IndirectReferenceToken reference)
                    {
                        context.Rewrites.Drop(source, reference.Data);
                    }
                }
            }
        }
    }

    private IndirectReferenceToken Information(AssemblyContext context)
    {
        if (options.Information is not null)
        {
            return context.Writer.WriteToken(new DictionaryToken(options.Information.ToDictionary()));
        }

        var primary = context.Primary;
        var information = primary.Resolve<DictionaryToken>(primary.Document.Structure.Trailer.Info) is { } info
            ? AssemblyTokens.Mutable((DictionaryToken)context.Copy(primary, info))
            : new Dictionary<NameToken, IToken>();

        information[AssemblyNames.ModificationDate] = new StringToken(PdfDate(DateTimeOffset.Now));
        return context.Writer.WriteToken(new DictionaryToken(information));
    }

    private static string PdfDate(DateTimeOffset when)
    {
        var offset = when.Offset;
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var magnitude = offset.Duration();
        return "D:" + when.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
            + sign + magnitude.Hours.ToString("00", CultureInfo.InvariantCulture)
            + "'" + magnitude.Minutes.ToString("00", CultureInfo.InvariantCulture) + "'";
    }
}
