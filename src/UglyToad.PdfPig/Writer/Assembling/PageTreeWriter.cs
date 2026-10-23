namespace UglyToad.PdfPig.Writer.Assembling;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Core;
using Geometry;
using Tokens;

/// <summary>
/// Writes each planned page at its reserved reference, with what it inherited from its source page tree made its own,
/// under a balanced page tree of its own. A sheet is written as a page of its own size that draws each page placed on
/// it from a form XObject of that page's content, clipped to the part of it shown.
/// </summary>
internal sealed class PageTreeWriter
{
    private const int KidsPerNode = 32;

    private static readonly NameToken[] Inheritable = [NameToken.Resources, NameToken.MediaBox, NameToken.CropBox, NameToken.Rotate];

    private readonly AssemblyContext context;
    private readonly AnnotationCarrier annotations;
    private readonly StructureCarrier structure;
    private readonly HashSet<IndirectReference> forms = new();

    public PageTreeWriter(AssemblyContext context, AnnotationCarrier annotations, StructureCarrier structure)
    {
        this.context = context;
        this.annotations = annotations;
        this.structure = structure;
    }

    /// <summary>
    /// Writes the pages and the tree over them; returns the root.
    /// </summary>
    public IndirectReferenceToken Write()
    {
        var level = new List<Node>();
        for (var start = 0; start < context.Sheets.Count; start += KidsPerNode)
        {
            var leaf = new Node(context.Writer.ReserveObjectNumber());
            foreach (var sheet in context.Sheets.Skip(start).Take(KidsPerNode))
            {
                context.ThrowIfCancelled();
                if (sheet.IsImposed)
                {
                    WriteSheet(sheet, leaf.Reference);
                }
                else
                {
                    WritePage(sheet.Slots[0], leaf.Reference);
                }

                leaf.Kids.Add(sheet.Reference);
                leaf.Count++;
            }

            level.Add(leaf);
        }

        var nodes = new List<Node>(level);
        while (level.Count > 1)
        {
            var above = new List<Node>();
            for (var start = 0; start < level.Count; start += KidsPerNode)
            {
                var parent = new Node(context.Writer.ReserveObjectNumber());
                foreach (var child in level.Skip(start).Take(KidsPerNode))
                {
                    child.Parent = parent.Reference;
                    parent.Kids.Add(child.Reference);
                    parent.Count += child.Count;
                }

                above.Add(parent);
            }

            nodes.AddRange(above);
            level = above;
        }

        foreach (var node in nodes)
        {
            var dictionary = new Dictionary<NameToken, IToken>
            {
                [NameToken.Type] = NameToken.Pages,
                [NameToken.Kids] = new ArrayToken(node.Kids),
                [NameToken.Count] = new NumericToken(node.Count),
            };

            if (node.Parent is not null)
            {
                dictionary[NameToken.Parent] = node.Parent;
            }

            context.Writer.WriteToken(new DictionaryToken(dictionary), node.Reference);
        }

        return level[0].Reference;
    }

    private void WritePage(AssemblySlot slot, IndirectReferenceToken parent)
    {
        var source = slot.Source;
        var page = slot.Page;
        var dictionary = new Dictionary<NameToken, IToken>();

        foreach (var entry in page.Dictionary.Data)
        {
            var name = NameToken.Create(entry.Key);
            if (name.Equals(NameToken.Type) || name.Equals(NameToken.Parent) || name.Equals(NameToken.StructParents))
            {
                continue;
            }

            if (name.Equals(NameToken.Annots))
            {
                var written = annotations.Written(context, slot);
                if (written.Count > 0)
                {
                    dictionary[NameToken.Annots] = new ArrayToken(written);
                }

                continue;
            }

            if (name.Equals(NameToken.B) && page.IsSplit)
            {
                var beads = Beads(slot);
                if (beads.Count > 0)
                {
                    dictionary[NameToken.B] = new ArrayToken(beads);
                }

                continue;
            }

            dictionary[name] = context.Copy(source, entry.Value);
        }

        foreach (var name in Inheritable)
        {
            if (!dictionary.ContainsKey(name) && page.Inherited(name) is { } inherited)
            {
                dictionary[name] = context.Copy(source, inherited);
            }
        }

        if (!dictionary.ContainsKey(NameToken.Resources))
        {
            dictionary[NameToken.Resources] = new DictionaryToken(new Dictionary<NameToken, IToken>());
        }

        if (slot.Region is { } region)
        {
            Show(dictionary, region);
        }

        structure.Place(context, slot, dictionary);

        dictionary[NameToken.Type] = NameToken.Page;
        dictionary[NameToken.Parent] = parent;
        context.Writer.WriteToken(new DictionaryToken(dictionary), slot.Reference);
    }

    /// <summary>
    /// Writes a sheet: a page of its own size whose content draws each page placed on it — the form of that page's
    /// content, put where the placement takes it and clipped to the part of it shown — and which holds what lies on each
    /// placement: its annotations and article beads.
    /// </summary>
    private void WriteSheet(AssemblySheet sheet, IndirectReferenceToken parent)
    {
        var xobjects = new Dictionary<NameToken, IToken>();
        var names = new Dictionary<IndirectReference, NameToken>();
        var content = new StringBuilder();
        var annotationsOnSheet = new List<IToken>();
        var beads = new List<IToken>();
        IToken? tabs = null;
        IToken? group = null;

        foreach (var slot in sheet.Slots)
        {
            var form = slot.Form!;
            var toSheet = slot.ToSheet!.Value;
            if (!names.TryGetValue(form.Data, out var name))
            {
                name = NameToken.Create("P" + names.Count.ToString(CultureInfo.InvariantCulture));
                names[form.Data] = name;
                xobjects[name] = form;
            }

            if (forms.Add(form.Data))
            {
                WriteForm(slot);
            }

            var clip = SheetGeometry.Bounds(toSheet, slot.Shown);
            content.Append("q ")
                .Append(Number(clip.Left)).Append(' ').Append(Number(clip.Bottom)).Append(' ')
                .Append(Number(clip.Width)).Append(' ').Append(Number(clip.Height)).Append(" re W n ")
                .Append(Number(toSheet.A)).Append(' ').Append(Number(toSheet.B)).Append(' ')
                .Append(Number(toSheet.C)).Append(' ').Append(Number(toSheet.D)).Append(' ')
                .Append(Number(toSheet.E)).Append(' ').Append(Number(toSheet.F)).Append(" cm /")
                .Append(name.Data).Append(" Do Q\n");

            annotationsOnSheet.AddRange(annotations.Written(context, slot));
            beads.AddRange(Beads(slot));

            if (tabs is null && slot.Page.Dictionary.TryGet(AssemblyNames.Tabs, out var pageTabs))
            {
                tabs = context.Copy(slot.Source, pageTabs);
            }

            if (group is null && slot.Page.Dictionary.TryGet(NameToken.Group, out var pageGroup))
            {
                group = context.Copy(slot.Source, pageGroup);
            }
        }

        var resources = new Dictionary<NameToken, IToken>();
        if (xobjects.Count > 0)
        {
            resources[NameToken.Xobject] = new DictionaryToken(xobjects);
        }

        var dictionary = new Dictionary<NameToken, IToken>
        {
            [NameToken.Type] = NameToken.Page,
            [NameToken.Parent] = parent,
            [NameToken.MediaBox] = SheetGeometry.Rectangle(sheet.Bounds!.Value),
            [NameToken.Resources] = new DictionaryToken(resources),
            [NameToken.Contents] = context.Writer.WriteToken(DataCompressor.CompressToStream(Encoding.ASCII.GetBytes(content.ToString()))),
        };

        if (annotationsOnSheet.Count > 0)
        {
            dictionary[NameToken.Annots] = new ArrayToken(annotationsOnSheet);
        }

        if (beads.Count > 0)
        {
            dictionary[NameToken.B] = new ArrayToken(beads);
        }

        if (tabs is not null)
        {
            dictionary[AssemblyNames.Tabs] = tabs;
        }

        if (group is not null)
        {
            dictionary[NameToken.Group] = group;
        }

        context.Writer.WriteToken(new DictionaryToken(dictionary), sheet.Reference);
    }

    /// <summary>
    /// Writes the form XObject a placed page is drawn from: its content, in its own user space over its media box, with
    /// the resources it inherits made its own and its transparency group — written once however often it is placed.
    /// A page's one content stream is carried as it is; several are read and joined, as a form holds only one.
    /// </summary>
    private void WriteForm(AssemblySlot slot)
    {
        var source = slot.Source;
        var page = slot.Page;
        var media = SheetGeometry.Rectangle(source, page.Inherited(NameToken.MediaBox)) ?? new PdfRectangle(0, 0, 612, 792);
        var dictionary = new Dictionary<NameToken, IToken>
        {
            [NameToken.Type] = NameToken.Xobject,
            [NameToken.Subtype] = NameToken.Form,
            [AssemblyNames.FormType] = new NumericToken(1),
            [NameToken.Bbox] = SheetGeometry.Rectangle(media),
            [NameToken.Resources] = page.Inherited(NameToken.Resources) is { } resources
                ? context.Copy(source, resources)
                : new DictionaryToken(new Dictionary<NameToken, IToken>()),
        };

        if (page.Dictionary.TryGet(NameToken.Group, out var group))
        {
            dictionary[NameToken.Group] = context.Copy(source, group);
        }

        var marking = structure.Form(context, slot);
        if (marking.Key is { } key)
        {
            dictionary[NameToken.StructParents] = new NumericToken(key);
        }

        var items = AssemblyTokens.Items(page.Dictionary.TryGet(NameToken.Contents, out var contents) ? contents : null, source.Scanner);
        if (items.Count == 1 && marking.Before is null && source.Resolve<StreamToken>(items[0]) is { } only)
        {
            var copied = (StreamToken)context.Copy(source, only);
            foreach (var entry in copied.StreamDictionary.Data)
            {
                var name = NameToken.Create(entry.Key);
                if (Carried.Contains(name))
                {
                    dictionary[name] = entry.Value;
                }
            }

            context.Writer.WriteToken(new StreamToken(new DictionaryToken(dictionary), copied.Data), slot.Form!);
            return;
        }

        using var joined = new MemoryStream();
        void Append(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            joined.Write(bytes, 0, bytes.Length);
        }

        if (marking.Before is not null)
        {
            Append(marking.Before);
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (source.Resolve<StreamToken>(items[i]) is not { } stream)
            {
                continue;
            }

            Memory<byte> decoded;
            try
            {
                decoded = stream.Decode(source.Document.Structure.FilterProvider, source.Scanner);
            }
            catch (Exception ex) when (!context.Cancellation.IsCancellationRequested)
            {
                throw new AssemblyRefusedException(
                    source.Document,
                    AssemblyRefusal.UnreadableContent,
                    $"Page {page.Number}'s content cannot be read to draw it on a sheet: {ex.Message}");
            }

            if (i > 0)
            {
                Append("\n");
            }

            joined.Write(decoded.ToArray(), 0, decoded.Length);
        }

        if (marking.After is not null)
        {
            Append(marking.After);
        }

        var compressed = DataCompressor.CompressBytes(joined.ToArray());
        dictionary[NameToken.Length] = new NumericToken(compressed.Length);
        dictionary[NameToken.Filter] = NameToken.FlateDecode;
        context.Writer.WriteToken(new StreamToken(new DictionaryToken(dictionary), compressed), slot.Form!);
    }

    /// <summary>
    /// The article beads the page shows where it is put: those lying in its part, when it is shown in more than one.
    /// </summary>
    private List<IToken> Beads(AssemblySlot slot)
        => (slot.Source.Resolve<ArrayToken>(slot.Page.Dictionary.TryGet(NameToken.B, out var beads) ? beads : null)?.Data ?? [])
            .Where(bead => context.IsOn(slot, bead))
            .Select(bead => context.Copy(slot.Source, bead))
            .ToList();

    private static string Number(double value) => value.ToString("0.##########", CultureInfo.InvariantCulture);

    /// <summary>
    /// Shows only <paramref name="region"/> of a page: it becomes the crop box, and the trim, bleed and art boxes are held
    /// to it — one that lies wholly outside it is the other part's, and goes.
    /// </summary>
    private static void Show(Dictionary<NameToken, IToken> page, PdfRectangle region)
    {
        page[NameToken.CropBox] = Rectangle(region);

        foreach (var name in Bounded)
        {
            if (!page.TryGetValue(name, out var box))
            {
                continue;
            }

            var held = Numbers(box) is { } written ? written.Intersect(region) : region;
            if (held is { } within)
            {
                page[name] = Rectangle(within);
            }
            else
            {
                page.Remove(name);
            }
        }
    }

    /// <summary>A box written as four numbers, as a rectangle; null when it is written otherwise.</summary>
    private static PdfRectangle? Numbers(IToken box)
    {
        if (box is not ArrayToken { Length: 4 } array || !array.Data.All(item => item is NumericToken))
        {
            return null;
        }

        var n = array.Data.Cast<NumericToken>().Select(number => number.Double).ToArray();
        return new PdfRectangle(System.Math.Min(n[0], n[2]), System.Math.Min(n[1], n[3]), System.Math.Max(n[0], n[2]), System.Math.Max(n[1], n[3]));
    }

    private static ArrayToken Rectangle(PdfRectangle rectangle) => new([
        new NumericToken(rectangle.Left), new NumericToken(rectangle.Bottom),
        new NumericToken(rectangle.Right), new NumericToken(rectangle.Top),
    ]);

    private static readonly NameToken[] Bounded = [NameToken.TrimBox, NameToken.BleedBox, NameToken.ArtBox];

    /// <summary>What a content stream carried as a form's own says about its bytes.</summary>
    private static readonly NameToken[] Carried = [NameToken.Length, NameToken.Filter, NameToken.DecodeParms, NameToken.Dl];

    private sealed class Node
    {
        public Node(IndirectReferenceToken reference)
        {
            Reference = reference;
        }

        public IndirectReferenceToken Reference { get; }

        public IndirectReferenceToken? Parent { get; set; }

        public List<IToken> Kids { get; } = new();

        public int Count { get; set; }
    }
}
