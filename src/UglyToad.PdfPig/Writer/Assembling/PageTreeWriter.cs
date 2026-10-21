namespace UglyToad.PdfPig.Writer.Assembling;

using System.Collections.Generic;
using System.Linq;
using Core;
using Geometry;
using Tokens;

/// <summary>
/// Writes each planned page at its reserved reference, with what it inherited from its source page tree made its own,
/// under a balanced page tree of its own.
/// </summary>
internal sealed class PageTreeWriter
{
    private const int KidsPerNode = 32;

    private static readonly NameToken[] Inheritable = [NameToken.Resources, NameToken.MediaBox, NameToken.CropBox, NameToken.Rotate];

    private readonly AssemblyContext context;
    private readonly AnnotationCarrier annotations;
    private readonly StructureCarrier structure;

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
        for (var start = 0; start < context.Slots.Count; start += KidsPerNode)
        {
            var leaf = new Node(context.Writer.ReserveObjectNumber());
            foreach (var slot in context.Slots.Skip(start).Take(KidsPerNode))
            {
                context.ThrowIfCancelled();
                WritePage(slot, leaf.Reference);
                leaf.Kids.Add(slot.Reference);
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
                var beads = (source.Resolve<ArrayToken>(entry.Value)?.Data ?? [])
                    .Where(bead => context.IsOn(slot, bead))
                    .Select(bead => context.Copy(source, bead))
                    .ToList();
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
            if (dictionary.ContainsKey(name))
            {
                continue;
            }

            for (var i = page.Ancestors.Count - 1; i >= 0; i--)
            {
                if (page.Ancestors[i].TryGet(name, out var inherited))
                {
                    dictionary[name] = context.Copy(source, inherited);
                    break;
                }
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
