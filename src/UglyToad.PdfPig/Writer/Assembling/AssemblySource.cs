namespace UglyToad.PdfPig.Writer.Assembling;

using System;
using System.Collections.Generic;
using Content;
using Core;
using Parser.Parts;
using Tokenization.Scanner;
using Tokens;

/// <summary>
/// A document pages are taken from: its pages in order, and the map from each of its objects to the object written
/// for it — the reference of a page kept, a claimed object's reserved reference, or the null object for one left out.
/// </summary>
internal sealed class AssemblySource
{
    private readonly Dictionary<int, SourcePage> pagesByNumber = new();
    private readonly Dictionary<IndirectReference, SourcePage> pagesByReference = new();
    private readonly Dictionary<IndirectReference, List<SourcePage>> annotationPages = new();
    private readonly List<IndirectReference> treeNodes = new();
    private readonly List<SourcePage> pages = new();

    public AssemblySource(PdfDocument document, int index)
    {
        Document = document;
        Index = index;
        Collect(document.Structure.Catalog.Pages.PageTree, new List<DictionaryToken>(), new HashSet<IndirectReference>());
    }

    public PdfDocument Document { get; }

    /// <summary>
    /// The source's place among the sources: 0 for the primary, the others in the order their first page appears.
    /// </summary>
    public int Index { get; }

    public bool IsPrimary => Index == 0;

    public IPdfTokenScanner Scanner => Document.Structure.TokenScanner;

    public DictionaryToken Catalog => Document.Structure.Catalog.CatalogDictionary;

    public IReadOnlyList<SourcePage> Pages => pages;

    /// <summary>
    /// Every node of the page tree, pages included. Nothing outside the page tree may copy one: the tree is rebuilt.
    /// </summary>
    public IReadOnlyList<IndirectReference> TreeNodes => treeNodes;

    /// <summary>
    /// Each source object already given an output reference.
    /// </summary>
    public Dictionary<IndirectReference, IndirectReferenceToken> References { get; } = new();

    /// <summary>
    /// Whether the pages kept appear in the order they had: when they do, the order of what follows the pages — the
    /// outline, the logical structure — is left as it was.
    /// </summary>
    public bool PreservesOrder { get; set; } = true;

    public SourcePage Page(int number)
        => pagesByNumber.TryGetValue(number, out var page)
            ? page
            : throw new ArgumentOutOfRangeException(nameof(number), number, $"The document has no page {number}.");

    public SourcePage? PageAt(IndirectReference reference)
        => pagesByReference.TryGetValue(reference, out var page) ? page : null;

    /// <summary>
    /// The pages whose <c>/Annots</c> list the annotation; none when it is on no page.
    /// </summary>
    public IReadOnlyList<SourcePage> PagesHolding(IndirectReference annotation)
        => annotationPages.TryGetValue(annotation, out var holding) ? holding : [];

    public T? Resolve<T>(IToken? token) where T : class, IToken
        => token is not null && DirectObjectFinder.TryGet(token, Scanner, out T? resolved) ? resolved : null;

    public DictionaryToken? Dictionary(DictionaryToken owner, NameToken key)
        => owner.TryGet(key, out var token) ? Resolve<DictionaryToken>(token) : null;

    public ArrayToken? Array(DictionaryToken owner, NameToken key)
        => owner.TryGet(key, out var token) ? Resolve<ArrayToken>(token) : null;

    private void Collect(PageTreeNode node, List<DictionaryToken> ancestors, HashSet<IndirectReference> visited)
    {
        if (!visited.Add(node.Reference))
        {
            return;
        }

        treeNodes.Add(node.Reference);

        if (node.IsPage)
        {
            var annotations = Array(node.NodeDictionary, NameToken.Annots)?.Data ?? [];
            var page = new SourcePage(node.PageNumber ?? pages.Count + 1, node.Reference, node.NodeDictionary, ancestors.ToArray(), annotations);

            pages.Add(page);
            pagesByNumber[page.Number] = page;
            pagesByReference[page.Reference] = page;

            foreach (var annotation in annotations)
            {
                if (annotation is not IndirectReferenceToken reference)
                {
                    continue;
                }

                if (!annotationPages.TryGetValue(reference.Data, out var holding))
                {
                    holding = new List<SourcePage>(1);
                    annotationPages[reference.Data] = holding;
                }

                holding.Add(page);
            }

            return;
        }

        ancestors.Add(node.NodeDictionary);
        foreach (var child in node.Children ?? [])
        {
            Collect(child, ancestors, visited);
        }

        ancestors.RemoveAt(ancestors.Count - 1);
    }
}
