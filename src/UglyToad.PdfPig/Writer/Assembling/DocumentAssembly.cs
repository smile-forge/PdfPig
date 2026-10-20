namespace UglyToad.PdfPig.Writer.Assembling;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Tokens;

/// <summary>
/// One assembly, start to end: the sources and the pages planned from them, what is left out, each carrier prepared,
/// the pages written, each carrier completed, the claimed objects written, and the catalog and trailer.
/// </summary>
internal sealed class DocumentAssembly
{
    private readonly IReadOnlyList<AssembledPage> pages;
    private readonly Stream output;
    private readonly AssemblyOptions options;
    private readonly CancellationToken cancellation;

    public DocumentAssembly(IReadOnlyList<AssembledPage> pages, Stream output, AssemblyOptions options, CancellationToken cancellation)
    {
        this.pages = pages;
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

        var slots = Plan(writer, byDocument);
        var context = new AssemblyContext(writer, sources, slots, cancellation);
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

    private List<AssemblySource> Sources(out Dictionary<PdfDocument, AssemblySource> byDocument)
    {
        var primary = options.Primary ?? pages[0].Document;
        var sources = new List<AssemblySource> { new(primary, 0) };
        byDocument = new Dictionary<PdfDocument, AssemblySource>(IdentityComparer<PdfDocument>.Instance) { [primary] = sources[0] };

        foreach (var page in pages)
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

    private List<AssemblySlot> Plan(IPdfStreamWriter writer, Dictionary<PdfDocument, AssemblySource> byDocument)
    {
        var slots = new List<AssemblySlot>(pages.Count);
        var lastNumber = new Dictionary<AssemblySource, int>();

        for (var i = 0; i < pages.Count; i++)
        {
            var assembled = pages[i];
            var source = byDocument[assembled.Document];
            var page = source.Page(assembled.PageNumber);

            if (page.IsKept)
            {
                throw new ArgumentException(
                    $"Page {assembled.PageNumber} of a document is assembled more than once; a page may appear only once.",
                    nameof(pages));
            }

            var slot = new AssemblySlot(i, source, page, writer.ReserveObjectNumber(), assembled.Tag);
            page.Slot = slot;
            source.References.Add(page.Reference, slot.Reference);
            slots.Add(slot);

            if (lastNumber.TryGetValue(source, out var last) && page.Number < last)
            {
                source.PreservesOrder = false;
            }

            lastNumber[source] = page.Number;
        }

        return slots;
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
