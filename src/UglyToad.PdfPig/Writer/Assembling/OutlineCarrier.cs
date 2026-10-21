namespace UglyToad.PdfPig.Writer.Assembling;

using System.Collections.Generic;
using System.Linq;
using Core;
using Tokens;

/// <summary>
/// The outline. An item whose page is left out goes with it, unless it has items under it still, when it stays as a
/// heading with no destination; where a source's pages were reordered its items follow their pages; a later source's
/// top items are placed among the primary's by page; and every item keeps its colour, style, structure element and
/// open or closed state, with its count worked out again.
/// </summary>
internal sealed class OutlineCarrier : IAssemblyCarrier
{
    private const int DeepestItem = 64;
    private const int MostItems = 1_000_000;

    private static readonly NameToken[] Rebuilt =
    [
        NameToken.Parent, NameToken.Prev, NameToken.Next, NameToken.First, NameToken.Last, NameToken.Count, NameToken.Dest, NameToken.A, NameToken.Se,
    ];

    public void Prepare(AssemblyContext context)
    {
    }

    public void Complete(AssemblyContext context, IDictionary<NameToken, IToken> catalog)
    {
        var primary = new List<Item>();
        var inserted = new List<Item>();

        foreach (var source in context.Sources)
        {
            if (source.Dictionary(source.Catalog, NameToken.Outlines) is not { } outlines)
            {
                continue;
            }

            var visited = new HashSet<IndirectReference>();
            var items = Kept(context, source, Read(context, source, outlines, 0, visited));
            (source.IsPrimary ? primary : inserted).AddRange(items);
        }

        var roots = AssemblyTokens.Interleave(primary, inserted, item => item.Page);
        if (roots.Count == 0)
        {
            return;
        }

        var outlinesReference = context.Writer.ReserveObjectNumber();
        var (first, last) = WriteLevel(context, roots, outlinesReference);

        catalog[NameToken.Outlines] = context.Writer.WriteToken(
            new DictionaryToken(new Dictionary<NameToken, IToken>
            {
                [NameToken.Type] = NameToken.Outlines,
                [NameToken.First] = first,
                [NameToken.Last] = last,
                [NameToken.Count] = new NumericToken(Visible(roots)),
            }),
            outlinesReference);
    }

    private static List<Item> Read(AssemblyContext context, AssemblySource source, DictionaryToken parent, int depth, HashSet<IndirectReference> visited)
    {
        var items = new List<Item>();
        if (depth > DeepestItem)
        {
            return items;
        }

        var token = parent.TryGet(NameToken.First, out var first) ? first : null;
        while (token is IndirectReferenceToken reference && visited.Count < MostItems && visited.Add(reference.Data)
               && source.Resolve<DictionaryToken>(token) is { } dictionary)
        {
            var target = dictionary.TryGet(NameToken.Dest, out var destination)
                ? context.Destinations.Resolve(source, destination)
                : context.Destinations.ResolveAction(source, dictionary.TryGet(NameToken.A, out var action) ? action : null);

            var item = new Item(source, dictionary, target)
            {
                Open = AssemblyTokens.Integer(dictionary.TryGet(NameToken.Count, out var count) ? count : null, source.Scanner) > 0,
            };
            item.Children.AddRange(Read(context, source, dictionary, depth + 1, visited));
            items.Add(item);

            token = dictionary.TryGet(NameToken.Next, out var next) ? next : null;
        }

        return items;
    }

    /// <summary>
    /// The items kept, each with its page settled and its kept items under it, in the order their pages now have
    /// where the source's pages were reordered.
    /// </summary>
    private static List<Item> Kept(AssemblyContext context, AssemblySource source, List<Item> items)
    {
        var kept = new List<Item>(items.Count);
        foreach (var item in items)
        {
            var children = Kept(context, source, item.Children);
            item.Children.Clear();
            item.Children.AddRange(children);

            item.LeftOut = context.IsPageLeftOut(source, item.Target.Page);
            if (item.LeftOut && item.Children.Count == 0)
            {
                continue;
            }

            item.Page = AssemblyTokens.Earliest(
                item.LeftOut ? null : context.SlotOf(source, item.Target)?.Index,
                item.Children.Aggregate((int?)null, (earliest, child) => AssemblyTokens.Earliest(earliest, child.Page)));
            kept.Add(item);
        }

        return source.PreservesOrder ? kept : AssemblyTokens.ByPage(kept, item => item.Page).ToList();
    }

    private static (IndirectReferenceToken First, IndirectReferenceToken Last) WriteLevel(AssemblyContext context, IReadOnlyList<Item> items, IndirectReferenceToken parent)
    {
        var references = items.Select(_ => context.Writer.ReserveObjectNumber()).ToList();
        for (var i = 0; i < items.Count; i++)
        {
            context.ThrowIfCancelled();
            WriteItem(context, items[i], references[i], parent, i > 0 ? references[i - 1] : null, i + 1 < items.Count ? references[i + 1] : null);
        }

        return (references[0], references[references.Count - 1]);
    }

    private static void WriteItem(
        AssemblyContext context,
        Item item,
        IndirectReferenceToken reference,
        IndirectReferenceToken parent,
        IndirectReferenceToken? previous,
        IndirectReferenceToken? next)
    {
        var source = item.Source;
        var dictionary = new Dictionary<NameToken, IToken>();
        foreach (var entry in item.Dictionary.Data)
        {
            var name = NameToken.Create(entry.Key);
            if (!Rebuilt.Contains(name))
            {
                dictionary[name] = context.Copy(source, entry.Value);
            }
        }

        if (!item.LeftOut)
        {
            var explicitTarget = !source.IsPrimary && item.Target.IsNamed ? item.Target.Explicit : null;
            if (item.Dictionary.TryGet(NameToken.Dest, out var destination))
            {
                dictionary[NameToken.Dest] = context.Copy(source, context.Retargeted(source, explicitTarget ?? destination, item.Target));
            }
            else if (item.Dictionary.TryGet(NameToken.A, out var action))
            {
                var goTo = source.Resolve<DictionaryToken>(action);
                var written = goTo is not null && goTo.TryGet(NameToken.D, out var d) ? d : null;
                var target = written is null ? null : context.Retargeted(source, explicitTarget ?? written, item.Target);
                dictionary[NameToken.A] = target is not null && !ReferenceEquals(target, written)
                    ? context.Copy(source, goTo!.With(NameToken.D, target))
                    : context.Copy(source, action);
            }
        }

        if (item.Dictionary.TryGet(NameToken.Se, out var element) && context.Copy(source, element) is var copiedElement && !context.IsNull(copiedElement))
        {
            dictionary[NameToken.Se] = copiedElement;
        }

        if (!dictionary.ContainsKey(NameToken.Title))
        {
            dictionary[NameToken.Title] = new StringToken(string.Empty);
        }

        dictionary[NameToken.Parent] = parent;
        if (previous is not null)
        {
            dictionary[NameToken.Prev] = previous;
        }

        if (next is not null)
        {
            dictionary[NameToken.Next] = next;
        }

        if (item.Children.Count > 0)
        {
            var (first, last) = WriteLevel(context, item.Children, reference);
            dictionary[NameToken.First] = first;
            dictionary[NameToken.Last] = last;

            var visible = Visible(item.Children);
            dictionary[NameToken.Count] = new NumericToken(item.Open ? visible : -visible);
        }

        context.Writer.WriteToken(new DictionaryToken(dictionary), reference);
    }

    /// <summary>
    /// The items shown when their parent is open: each one, and those under each that is open.
    /// </summary>
    private static int Visible(IReadOnlyList<Item> items)
        => items.Sum(item => 1 + (item.Open ? Visible(item.Children) : 0));

    private sealed class Item
    {
        public Item(AssemblySource source, DictionaryToken dictionary, ResolvedDestination target)
        {
            Source = source;
            Dictionary = dictionary;
            Target = target;
        }

        public AssemblySource Source { get; }

        public DictionaryToken Dictionary { get; }

        public ResolvedDestination Target { get; }

        public List<Item> Children { get; } = new();

        public bool Open { get; set; }

        /// <summary>
        /// Whether the item's page is left out: it stays only as a heading over the items under it.
        /// </summary>
        public bool LeftOut { get; set; }

        /// <summary>
        /// The earliest output page of the item and the items under it.
        /// </summary>
        public int? Page { get; set; }
    }
}
