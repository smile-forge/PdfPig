namespace UglyToad.PdfPig.Writer.Assembling;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Content;
using Core;
using Geometry;
using Graphics;
using Logging;
using Parser.Parts;
using Tokens;

/// <summary>
/// The logical structure of tagged sources. Each structure element keeps the content on the pages kept and goes when
/// none is left; where a source's pages were reordered its elements follow their pages; the trees of several sources
/// become one, a later source's elements placed among the first's by page, with clashing roles, classes and IDs
/// renamed apart; the parent tree is rebuilt with each source's keys moved past the last's; and an untagged page given
/// a tag is wrapped as one element of its own.
/// </summary>
internal sealed class StructureCarrier : IAssemblyCarrier
{
    private const int DeepestElement = 256;
    private const int DeepestResources = 32;

    private static readonly NameToken[] Built =
    [
        NameToken.Type, NameToken.K, NameToken.ParentTree, NameToken.ParentTreeNextKey, NameToken.RoleMap, AssemblyNames.ClassMap,
        NameToken.IdTree, AssemblyNames.Namespaces, AssemblyNames.PronunciationLexicon, AssemblyNames.AssociatedFiles,
    ];

    private static readonly NameToken[] UnionedArrays = [AssemblyNames.Namespaces, AssemblyNames.PronunciationLexicon, AssemblyNames.AssociatedFiles];

    private readonly AnnotationCarrier annotations;
    private readonly List<Tree> trees = new();
    private readonly Dictionary<AssemblySource, Tree> treesBySource = new();
    private readonly Dictionary<IndirectReference, Figure> figures = new();
    private readonly Dictionary<string, IToken> roles = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (AssemblySource Source, IToken Value)> classes = new(StringComparer.Ordinal);
    private readonly List<KeyValuePair<IDataToken<string>, (AssemblySource Source, IToken Element)>> ids = new();
    private readonly HashSet<string> idNames = new(StringComparer.Ordinal);
    private readonly List<Placed> rootKids = new();
    private readonly List<Placed> placedDirectly = new();
    private readonly Dictionary<IndirectReference, (int Key, int SourceKey, Tree Tree)> holderKeys = new();
    private readonly Dictionary<(SourcePage Page, int Mcid), AssemblySlot> contentParts = new();
    private IndirectReferenceToken? root;
    private int nextKey;

    public StructureCarrier(AnnotationCarrier annotations)
    {
        this.annotations = annotations;
    }

    public void Prepare(AssemblyContext context)
    {
        foreach (var source in context.Sources)
        {
            if (source.Catalog.TryGet(NameToken.StructTreeRoot, out var token) && source.Resolve<DictionaryToken>(token) is { } dictionary)
            {
                var tree = new Tree(source, dictionary);
                trees.Add(tree);
                treesBySource[source] = tree;
            }
        }

        if (trees.Count == 0)
        {
            return;
        }

        root = context.Writer.ReserveObjectNumber();

        foreach (var tree in trees)
        {
            context.ThrowIfCancelled();

            if (tree.Source.Catalog.TryGet(NameToken.StructTreeRoot, out var rootToken) && rootToken is IndirectReferenceToken sourceRoot)
            {
                context.Rewrites.Redirect(tree.Source, sourceRoot.Data, root);
            }

            Number(tree);
            KeepKeys(context, tree);
            MergeRoles(tree);
            MergeClasses(tree);

            var visited = new HashSet<IndirectReference>();
            foreach (var kid in AssemblyTokens.Items(tree.Root.TryGet(NameToken.K, out var k) ? k : null, tree.Source.Scanner))
            {
                if (Visit(context, tree, kid, null, 0, visited) is { } kept)
                {
                    tree.Top.Add(kept);
                }
            }

            if (!tree.Source.PreservesOrder)
            {
                tree.Top = AssemblyTokens.ByPage(tree.Top, kid => kid.Page).ToList();
            }

            MergeIds(context, tree);
            Speak(context, tree);
        }

        var keyed = new HashSet<SourcePage>();
        foreach (var slot in context.Slots)
        {
            if (slot.Tag is not null && !treesBySource.ContainsKey(slot.Source) && !figures.ContainsKey(slot.Holder))
            {
                figures[slot.Holder] = new Figure(slot, context.Writer.ReserveObjectNumber(), nextKey++);
            }

            // Each place a page's content is drawn from names its marked content with a key of its own — the page
            // written as itself, each further part written as a page, the one form every placement of it on a sheet
            // draws — the first keeping the page's.
            if (treesBySource.TryGetValue(slot.Source, out var tree) && !holderKeys.ContainsKey(slot.Holder)
                && AssemblyTokens.Integer(slot.Page.Dictionary.TryGet(NameToken.StructParents, out var key) ? key : null, slot.Source.Scanner) is { } sourceKey)
            {
                holderKeys[slot.Holder] = (keyed.Add(slot.Page) ? sourceKey + tree.Offset : nextKey++, sourceKey, tree);
            }
        }

        PlaceTopElements(context);
    }

    /// <summary>
    /// Gives a page being written its key in the parent tree, and wraps the content of an untagged page given a tag
    /// as the one element its figure is.
    /// </summary>
    public void Place(AssemblyContext context, AssemblySlot slot, Dictionary<NameToken, IToken> page)
    {
        if (root is null)
        {
            return;
        }

        if (treesBySource.ContainsKey(slot.Source))
        {
            if (holderKeys.TryGetValue(slot.Holder, out var held))
            {
                page[NameToken.StructParents] = new NumericToken(held.Key);
            }

            return;
        }

        if (!figures.TryGetValue(slot.Holder, out var figure))
        {
            return;
        }

        var contents = new List<IToken> { ContentStream(context, "/" + Escaped(figure.Slot.Tag!.Role) + " <</MCID 0>> BDC\n") };
        foreach (var item in AssemblyTokens.Items(slot.Page.Dictionary.TryGet(NameToken.Contents, out var c) ? c : null, slot.Source.Scanner))
        {
            contents.Add(context.Copy(slot.Source, item));
        }

        contents.Add(ContentStream(context, "\nEMC\n"));
        page[NameToken.Contents] = new ArrayToken(contents);
        page[NameToken.StructParents] = new NumericToken(figure.Key);
    }

    /// <summary>
    /// How the form a placed page is drawn from is named in the structure: its key in the parent tree, and for an untagged
    /// page given a tag, what its content is wrapped in to be the one element its figure is.
    /// </summary>
    public FormMarking Form(AssemblyContext context, AssemblySlot slot)
    {
        if (root is null)
        {
            return default;
        }

        if (treesBySource.ContainsKey(slot.Source))
        {
            return holderKeys.TryGetValue(slot.Holder, out var held) ? new FormMarking(held.Key, null, null) : default;
        }

        return figures.TryGetValue(slot.Holder, out var figure)
            ? new FormMarking(figure.Key, "/" + Escaped(figure.Slot.Tag!.Role) + " <</MCID 0>> BDC\n", "\nEMC\n")
            : default;
    }

    public void Complete(AssemblyContext context, IDictionary<NameToken, IToken> catalog)
    {
        if (root is null)
        {
            return;
        }

        foreach (var figure in figures.Values)
        {
            var element = new Dictionary<NameToken, IToken>
            {
                [NameToken.Type] = AssemblyNames.StructureElement,
                [NameToken.S] = NameToken.Create(figure.Slot.Tag!.Role),
                [NameToken.P] = figure.Parent!,
                [NameToken.Pg] = figure.Slot.Reference,
                [NameToken.K] = figure.Slot.Form is { } form
                    ? new DictionaryToken(new Dictionary<NameToken, IToken>
                    {
                        [NameToken.Type] = AssemblyNames.MarkedContentReference,
                        [NameToken.Pg] = figure.Slot.Reference,
                        [AssemblyNames.Stm] = form,
                        [NameToken.Mcid] = NumericToken.Zero,
                    })
                    : NumericToken.Zero,
            };

            if (figure.Slot.Tag.AlternateDescription is { } alternate)
            {
                element[NameToken.Alt] = new StringToken(alternate);
            }

            context.Writer.WriteToken(new DictionaryToken(element), figure.Output);
        }

        foreach (var placed in placedDirectly)
        {
            var copied = (DictionaryToken)context.Copy(placed.Source!, placed.Direct!);
            context.Writer.WriteToken(copied.With(NameToken.P, placed.Parent!), placed.Output);
        }

        var dictionary = new Dictionary<NameToken, IToken>();
        if (trees[0].Source.IsPrimary)
        {
            foreach (var entry in trees[0].Root.Data)
            {
                var name = NameToken.Create(entry.Key);
                if (!Built.Contains(name))
                {
                    dictionary[name] = context.Copy(trees[0].Source, entry.Value);
                }
            }
        }

        dictionary[NameToken.Type] = NameToken.StructTreeRoot;
        dictionary[NameToken.K] = new ArrayToken(rootKids.Select(kid => (IToken)kid.Output).ToList());
        dictionary[NameToken.ParentTree] = context.Writer.WriteToken(ParentTree(context));
        dictionary[NameToken.ParentTreeNextKey] = new NumericToken(nextKey);

        if (roles.Count > 0)
        {
            dictionary[NameToken.RoleMap] = new DictionaryToken(roles.ToDictionary(role => NameToken.Create(role.Key), role => role.Value));
        }

        if (classes.Count > 0)
        {
            dictionary[AssemblyNames.ClassMap] = new DictionaryToken(
                classes.ToDictionary(entry => NameToken.Create(entry.Key), entry => context.Copy(entry.Value.Source, entry.Value.Value)));
        }

        var idEntries = ids
            .Select(entry => new KeyValuePair<IDataToken<string>, IToken>(entry.Key, context.Copy(entry.Value.Source, entry.Value.Element)))
            .Where(entry => !context.IsNull(entry.Value))
            .ToList();
        if (idEntries.Count > 0)
        {
            dictionary[NameToken.IdTree] = context.Writer.WriteToken(NameTreeEntries.Leaf(idEntries));
        }

        foreach (var key in UnionedArrays)
        {
            var items = trees
                .SelectMany(tree => (tree.Source.Array(tree.Root, key)?.Data ?? []).Select(item => context.Copy(tree.Source, item)))
                .Where(item => !context.IsNull(item))
                .Distinct()
                .ToList();
            if (items.Count > 0)
            {
                dictionary[key] = new ArrayToken(items);
            }
        }

        context.Writer.WriteToken(new DictionaryToken(dictionary), root);
        catalog[NameToken.StructTreeRoot] = root;
        catalog[NameToken.MarkInfo] = MarkInfo(context);
    }

    /// <summary>
    /// Moves the source's parent tree keys past those of the sources before it.
    /// </summary>
    private void Number(Tree tree)
    {
        tree.Offset = nextKey;

        var span = 0;
        if (tree.Source.Dictionary(tree.Root, NameToken.ParentTree) is { } parentTree)
        {
            tree.ParentTree = NumberTreeParser.Flatten(parentTree, tree.Source.Scanner);
            if (tree.ParentTree.Count > 0)
            {
                span = tree.ParentTree[tree.ParentTree.Count - 1].Key + 1;
            }
        }

        var declared = AssemblyTokens.Integer(tree.Root.TryGet(NameToken.ParentTreeNextKey, out var next) ? next : null, tree.Source.Scanner) ?? 0;
        nextKey = tree.Offset + Math.Max(span, declared);
    }

    /// <summary>
    /// The parent tree keys still in use — those of the pages kept, their annotations and the form XObjects they
    /// draw — each object's key moved by the source's offset.
    /// </summary>
    private void KeepKeys(AssemblyContext context, Tree tree)
    {
        var source = tree.Source;
        var visited = new HashSet<IndirectReference>();

        foreach (var page in context.Slots.Where(slot => slot.Source == source).Select(slot => slot.Page).Distinct())
        {
            if (AssemblyTokens.Integer(page.Dictionary.TryGet(NameToken.StructParents, out var key) ? key : null, source.Scanner) is { } pageKey)
            {
                tree.Kept.Add(pageKey);
            }

            if (page.IsSplit)
            {
                LocateContent(context, tree, page);
            }

            Resources(context, tree, Inherited(page, NameToken.Resources), visited, 0);

            foreach (var entry in page.Annotations)
            {
                if (entry is IndirectReferenceToken reference && !context.KeepsAnnotation(source, reference.Data)
                    || source.Resolve<DictionaryToken>(entry) is not { } annotation)
                {
                    continue;
                }

                if (AssemblyTokens.Integer(annotation.TryGet(NameToken.StructParent, out var parent) ? parent : null, source.Scanner) is { } annotationKey)
                {
                    tree.Kept.Add(annotationKey);
                    if (tree.Offset != 0)
                    {
                        var moved = annotationKey + tree.Offset;
                        Func<DictionaryToken, DictionaryToken> edit = d => d.With(NameToken.StructParent, new NumericToken(moved));
                        if (entry is IndirectReferenceToken annotationReference)
                        {
                            context.Rewrites.Before(source, annotationReference.Data, edit);
                        }
                        else
                        {
                            annotations.Adjust(annotation, edit);
                        }
                    }
                }

                if (source.Dictionary(annotation, NameToken.Ap) is { } appearances)
                {
                    foreach (var appearance in appearances.Data.Values)
                    {
                        XObject(context, tree, appearance, visited, 0);
                        if (source.Resolve<DictionaryToken>(appearance) is { } states)
                        {
                            foreach (var state in states.Data.Values)
                            {
                                XObject(context, tree, state, visited, 0);
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Finds which part of a split page each of its marked contents lies in: the part holding the centre of what it
    /// paints — its glyphs, paths and images — taken back to the page's user space, where the parts are.
    /// </summary>
    private void LocateContent(AssemblyContext context, Tree tree, SourcePage page)
    {
        Page read;
        try
        {
            read = tree.Source.Document.GetPage(page.Number);
        }
        catch (Exception) when (!context.Cancellation.IsCancellationRequested)
        {
            return;   // a page whose content cannot be read keeps its content on its first part
        }

        var toUser = OperationContextHelper.GetInitialMatrix(UserSpaceUnit.Default, read.CropBox, read.Rotation, new NoOpLog()).Inverse();
        var pending = new Stack<MarkedContentElement>(read.GetMarkedContents());
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            foreach (var child in element.Children)
            {
                pending.Push(child);
            }

            if (element.MarkedContentIdentifier < 0 || contentParts.ContainsKey((page, element.MarkedContentIdentifier)))
            {
                continue;
            }

            var bounds = element.Letters.Select(letter => (PdfRectangle?)letter.GlyphRectangle)
                .Concat(element.Paths.Select(path => path.GetBoundingRectangle()))
                .Concat(element.Images.Select(image => (PdfRectangle?)image.Bounds))
                .Where(rectangle => rectangle.HasValue)
                .Select(rectangle => rectangle!.Value)
                .ToList();
            if (bounds.Count == 0)
            {
                continue;
            }

            var centre = toUser.Transform(new PdfPoint(
                (bounds.Min(b => b.Left) + bounds.Max(b => b.Right)) / 2,
                (bounds.Min(b => b.Bottom) + bounds.Max(b => b.Top)) / 2));
            if (page.SlotAt(centre.X, centre.Y) is { } slot)
            {
                contentParts[(page, element.MarkedContentIdentifier)] = slot;
            }
        }
    }

    /// <summary>
    /// Where a marked content of a page is drawn from: the part of a split page it lies in, else the first place the page
    /// goes; null where the page is not kept.
    /// </summary>
    private AssemblySlot? HolderOf(AssemblySource source, IndirectReference? page, int mcid)
        => page.HasValue && source.PageAt(page.Value) is { IsKept: true } kept
            ? kept.IsSplit && contentParts.TryGetValue((kept, mcid), out var slot) ? slot : kept.Slot
            : null;

    /// <summary>
    /// A marked-content reference named with where its content is drawn from: the page it is on, and the form a placed
    /// page is drawn from.
    /// </summary>
    private static DictionaryToken Named(AssemblyContext context, AssemblySource source, AssemblySlot holder, DictionaryToken reference)
    {
        var named = reference.With(NameToken.Pg, context.Rewrites.Placeholder(source, holder.Reference));
        return holder.Form is { } form ? named.With(AssemblyNames.Stm, context.Rewrites.Placeholder(source, form)) : named;
    }

    private void Resources(AssemblyContext context, Tree tree, IToken? resources, HashSet<IndirectReference> visited, int depth)
    {
        if (depth > DeepestResources
            || tree.Source.Resolve<DictionaryToken>(resources) is not { } dictionary
            || tree.Source.Dictionary(dictionary, NameToken.Xobject) is not { } xobjects)
        {
            return;
        }

        foreach (var xobject in xobjects.Data.Values)
        {
            XObject(context, tree, xobject, visited, depth);
        }
    }

    private void XObject(AssemblyContext context, Tree tree, IToken token, HashSet<IndirectReference> visited, int depth)
    {
        if (token is not IndirectReferenceToken reference
            || !visited.Add(reference.Data)
            || tree.Source.Resolve<StreamToken>(token) is not { } stream
            || !AssemblyTokens.IsName(stream.StreamDictionary, NameToken.Subtype, NameToken.Form, tree.Source.Scanner))
        {
            return;
        }

        if (AssemblyTokens.Integer(stream.StreamDictionary.TryGet(NameToken.StructParents, out var key) ? key : null, tree.Source.Scanner) is { } formKey)
        {
            tree.Kept.Add(formKey);
            if (tree.Offset != 0)
            {
                var moved = formKey + tree.Offset;
                context.Rewrites.Before(tree.Source, reference.Data, d => d.With(NameToken.StructParents, new NumericToken(moved)));
            }
        }

        Resources(context, tree, stream.StreamDictionary.TryGet(NameToken.Resources, out var resources) ? resources : null, visited, depth + 1);
    }

    private void MergeRoles(Tree tree)
    {
        if (tree.Source.Dictionary(tree.Root, NameToken.RoleMap) is not { } roleMap)
        {
            return;
        }

        foreach (var entry in roleMap.Data)
        {
            if (!roles.TryGetValue(entry.Key, out var existing))
            {
                roles[entry.Key] = entry.Value;
            }
            else if (!existing.Equals(entry.Value))
            {
                var renamed = Unique(entry.Key, roles.ContainsKey);
                roles[renamed] = entry.Value;
                tree.Roles[entry.Key] = renamed;
            }
        }
    }

    private void MergeClasses(Tree tree)
    {
        if (tree.Source.Dictionary(tree.Root, AssemblyNames.ClassMap) is not { } classMap)
        {
            return;
        }

        foreach (var entry in classMap.Data)
        {
            var name = classes.ContainsKey(entry.Key) ? Unique(entry.Key, classes.ContainsKey) : entry.Key;
            classes[name] = (tree.Source, entry.Value);
            if (name != entry.Key)
            {
                tree.Classes[entry.Key] = name;
            }
        }
    }

    private void MergeIds(AssemblyContext context, Tree tree)
    {
        if (tree.Source.Dictionary(tree.Root, NameToken.IdTree) is not { } idTree)
        {
            return;
        }

        foreach (var entry in NameTreeEntries.Read(idTree, tree.Source.Scanner))
        {
            if (entry.Value is not IndirectReferenceToken element || context.Rewrites.IsDropped(tree.Source, element.Data))
            {
                continue;
            }

            var key = entry.Key;
            if (!idNames.Add(key.Data))
            {
                var renamed = Unique(key.Data, idNames.Contains);
                idNames.Add(renamed);
                key = new StringToken(renamed);
                context.Rewrites.Before(tree.Source, element.Data, d => d.With(NameToken.Id, new StringToken(renamed)));
            }

            ids.Add(new KeyValuePair<IDataToken<string>, (AssemblySource, IToken)>(key, (tree.Source, element)));
        }
    }

    /// <summary>
    /// Gives a later source's top elements its language, where it differs from the primary's and they name none.
    /// </summary>
    private static void Speak(AssemblyContext context, Tree tree)
    {
        if (tree.Source.IsPrimary)
        {
            return;
        }

        var language = AssemblyTokens.Text(tree.Source.Catalog.TryGet(NameToken.Lang, out var lang) ? lang : null, tree.Source.Scanner);
        var primary = AssemblyTokens.Text(context.Primary.Catalog.TryGet(NameToken.Lang, out var primaryLang) ? primaryLang : null, context.Primary.Scanner);
        if (language is null || language == primary)
        {
            return;
        }

        for (var i = 0; i < tree.Top.Count; i++)
        {
            var kid = tree.Top[i];
            if (kid.Element is null || kid.Element.ContainsKey(NameToken.Lang))
            {
                continue;
            }

            tree.Top[i] = kid.With(context.Rewrites.Apply(tree.Source, kid.Token, [d => d.With(NameToken.Lang, new StringToken(language))]));
        }
    }

    /// <summary>
    /// Walks a structure element's content, leaving out what is on pages left out and an element left with none.
    /// </summary>
    private Kid? Visit(AssemblyContext context, Tree tree, IToken token, IndirectReference? page, int depth, HashSet<IndirectReference> visited)
    {
        var source = tree.Source;

        if (token is NumericToken mcid)
        {
            // Marked content on a later part of a split page is named with that part, and on a page placed on a sheet
            // with the sheet and the form it is drawn from, as a reference of its own.
            return HolderOf(source, page, mcid.Int) is { } holder && (holder.IsPlaced || holder != holder.Page.Slot)
                ? new Kid(
                    Named(context, source, holder, new DictionaryToken(new Dictionary<NameToken, IToken>
                    {
                        [NameToken.Type] = AssemblyNames.MarkedContentReference,
                        [NameToken.Mcid] = mcid,
                    })),
                    null, holder.Index, [])
                : Content(context, source, token, page);
        }

        if (token is not IndirectReferenceToken && token is not DictionaryToken || source.Resolve<DictionaryToken>(token) is not { } dictionary)
        {
            return new Kid(token, null, null, []);
        }

        if (AssemblyTokens.IsName(dictionary, NameToken.Type, AssemblyNames.MarkedContentReference, source.Scanner))
        {
            // One naming content in a form the page itself draws keeps its form; only its page moves, with the page.
            var contentPage = PageOf(dictionary) ?? page;
            return !dictionary.ContainsKey(AssemblyNames.Stm)
                   && AssemblyTokens.Integer(dictionary.TryGet(NameToken.Mcid, out var id) ? id : null, source.Scanner) is { } number
                   && HolderOf(source, contentPage, number) is { } holder && (holder.IsPlaced || holder != holder.Page.Slot)
                ? new Kid(Named(context, source, holder, dictionary), null, holder.Index, [])
                : Content(context, source, token, contentPage);
        }

        if (AssemblyTokens.IsName(dictionary, NameToken.Type, AssemblyNames.ObjectReference, source.Scanner))
        {
            var objectPage = PageOf(dictionary) ?? page;
            var objectReference = dictionary.TryGet(NameToken.Obj, out var obj) ? obj as IndirectReferenceToken : null;
            if (objectReference is not null && context.Rewrites.IsDropped(source, objectReference.Data))
            {
                return null;
            }

            // An object on a later part of a split page — an annotation — is named with that part.
            return objectReference is not null && context.PlacedOn(source, objectReference.Data) is { } part && part != part.Page.Slot
                ? new Kid(dictionary.With(NameToken.Pg, context.Rewrites.Placeholder(source, part.Reference)), null, part.Index, [])
                : Content(context, source, token, objectPage);
        }

        var reference = token as IndirectReferenceToken;
        if (reference is not null && !visited.Add(reference.Data))
        {
            return null;
        }

        if (depth > DeepestElement)
        {
            return new Kid(token, dictionary, null, []);
        }

        var elementPage = PageOf(dictionary) ?? page;
        var kids = AssemblyTokens.Items(dictionary.TryGet(NameToken.K, out var k) ? k : null, source.Scanner);
        var kept = new List<Kid>(kids.Count);
        foreach (var kid in kids)
        {
            if (Visit(context, tree, kid, elementPage, depth + 1, visited) is { } keptKid)
            {
                kept.Add(keptKid);
            }
        }

        if (kids.Count > 0 && kept.Count == 0)
        {
            if (reference is not null)
            {
                context.Rewrites.Drop(source, reference.Data);
            }

            return null;
        }

        var ordered = source.PreservesOrder ? kept : AssemblyTokens.ByPage(kept, kid => kid.Page).ToList();
        var edits = new List<Func<DictionaryToken, DictionaryToken>>();

        var changed = ordered.Count != kids.Count;
        for (var i = 0; !changed && i < ordered.Count; i++)
        {
            changed = !ReferenceEquals(ordered[i].Token, kids[i]);
        }

        if (changed)
        {
            var tokens = ordered.Select(kid => kid.Token).ToList();
            edits.Add(d => d.With(NameToken.K, new ArrayToken(tokens)));
        }

        if (AssemblyTokens.Name(dictionary.TryGet(NameToken.S, out var role) ? role : null, source.Scanner) is { } roleName
            && tree.Roles.TryGetValue(roleName.Data, out var renamedRole))
        {
            edits.Add(d => d.With(NameToken.S, NameToken.Create(renamedRole)));
        }

        if (tree.Classes.Count > 0 && dictionary.TryGet(NameToken.C, out var classToken))
        {
            var renamedClasses = RenamedClasses(tree, source.Resolve<IToken>(classToken));
            if (renamedClasses is not null)
            {
                edits.Add(d => d.With(NameToken.C, renamedClasses));
            }
        }

        var first = ordered.Aggregate((int?)null, (earliest, kid) => AssemblyTokens.Earliest(earliest, kid.Page));
        return new Kid(context.Rewrites.Apply(source, token, edits), dictionary, first, ordered.Select(kid => kid.Page).ToList());
    }

    private static Kid? Content(AssemblyContext context, AssemblySource source, IToken token, IndirectReference? page)
        => context.IsPageLeftOut(source, page) ? null : new Kid(token, null, context.PositionOf(source, page), []);

    private static IndirectReference? PageOf(DictionaryToken dictionary)
        => dictionary.TryGet(NameToken.Pg, out var page) && page is IndirectReferenceToken reference ? reference.Data : null;

    private static IToken? RenamedClasses(Tree tree, IToken? classes)
    {
        switch (classes)
        {
            case NameToken name when tree.Classes.TryGetValue(name.Data, out var renamed):
                return NameToken.Create(renamed);
            case ArrayToken array when array.Data.Any(item => item is NameToken n && tree.Classes.ContainsKey(n.Data)):
                return new ArrayToken(array.Data
                    .Select(item => item is NameToken n && tree.Classes.TryGetValue(n.Data, out var renamed) ? NameToken.Create(renamed) : item)
                    .ToList());
            default:
                return null;
        }
    }

    /// <summary>
    /// Decides where each top element goes. When the primary tree has one top element — a document — the other
    /// sources' top elements and the figures go inside it, placed by page; otherwise all go at the root.
    /// </summary>
    private void PlaceTopElements(AssemblyContext context)
    {
        var primary = trees[0].Source.IsPrimary ? trees[0] : null;
        var inserted = new List<Placed>();
        foreach (var tree in trees.Where(tree => tree != primary))
        {
            inserted.AddRange(tree.Top.Select(kid => Placement(context, tree.Source, kid)));
        }

        inserted.AddRange(figures.Values.OrderBy(figure => figure.Slot.Index).Select(figure => new Placed(figure.Output, figure.Slot.Index, figure)));

        if (primary is { Top.Count: 1 } && primary.Top[0].Token is IndirectReferenceToken container)
        {
            var containerKid = primary.Top[0];
            var containerOutput = context.Rewrites.Claim(primary.Source, container.Data);
            var containerKids = AssemblyTokens.Items(containerKid.Element!.TryGet(NameToken.K, out var k) ? k : null, primary.Source.Scanner);
            var keptKids = containerKid.KidPages;

            // The container's kids are listed afresh, so the copied list lines up with their pages.
            context.Rewrites.Before(primary.Source, container.Data, d => d.With(NameToken.K, new ArrayToken(AssemblyTokens.Items(d.TryGet(NameToken.K, out var current) ? current : null, primary.Source.Scanner).ToList())));
            context.Rewrites.After(primary.Source, container.Data, copied =>
            {
                var items = copied.TryGet(NameToken.K, out var copiedKids) && copiedKids is ArrayToken array ? array.Data : [];
                var placed = items.Select((item, index) => (Token: item, Page: index < keptKids.Count ? keptKids[index] : null)).ToList();
                var merged = AssemblyTokens.Interleave(placed, inserted.Select(kid => (Token: (IToken)kid.Output, kid.Page)), kid => kid.Page);
                return copied.With(NameToken.K, new ArrayToken(merged.Select(kid => kid.Token).ToList())).With(NameToken.P, root!);
            });

            foreach (var kid in inserted)
            {
                Parent(context, kid, containerOutput);
            }

            rootKids.Add(new Placed(containerOutput, containerKid.Page, null));
            return;
        }

        var primaryKids = primary?.Top.Select(kid => Placement(context, primary.Source, kid)).ToList() ?? [];
        rootKids.AddRange(AssemblyTokens.Interleave(primaryKids, inserted, kid => kid.Page));
        foreach (var kid in rootKids)
        {
            Parent(context, kid, root!);
        }
    }

    private Placed Placement(AssemblyContext context, AssemblySource source, Kid kid)
    {
        if (kid.Token is IndirectReferenceToken reference)
        {
            return new Placed(context.Rewrites.Claim(source, reference.Data), kid.Page, null) { Source = source, Reference = reference.Data };
        }

        var placed = new Placed(context.Writer.ReserveObjectNumber(), kid.Page, null) { Source = source, Direct = kid.Token };
        placedDirectly.Add(placed);
        return placed;
    }

    private static void Parent(AssemblyContext context, Placed kid, IndirectReferenceToken parent)
    {
        if (kid.Figure is not null)
        {
            kid.Figure.Parent = parent;
        }
        else if (kid.Reference is { } reference)
        {
            context.Rewrites.After(kid.Source!, reference, copied => copied.With(NameToken.P, parent));
        }
        else
        {
            kid.Parent = parent;
        }
    }

    private DictionaryToken ParentTree(AssemblyContext context)
    {
        var entries = new SortedDictionary<int, IToken>();
        foreach (var tree in trees)
        {
            foreach (var entry in tree.ParentTree)
            {
                if (!tree.Kept.Contains(entry.Key))
                {
                    continue;
                }

                var value = ParentTreeValue(context, tree.Source, entry.Value);
                if (value is not null)
                {
                    entries[entry.Key + tree.Offset] = value;
                }
            }
        }

        foreach (var figure in figures.Values)
        {
            entries[figure.Key] = new ArrayToken([figure.Output]);
        }

        foreach (var held in holderKeys.Values)
        {
            if (held.Key == held.SourceKey + held.Tree.Offset)
            {
                continue;
            }

            var source = held.Tree.ParentTree.FirstOrDefault(entry => entry.Key == held.SourceKey);
            if (source.Value is not null && ParentTreeValue(context, held.Tree.Source, source.Value) is { } value)
            {
                entries[held.Key] = value;
            }
        }

        var numbers = new List<IToken>(entries.Count * 2);
        foreach (var entry in entries)
        {
            numbers.Add(new NumericToken(entry.Key));
            numbers.Add(entry.Value);
        }

        return new DictionaryToken(new Dictionary<NameToken, IToken> { [NameToken.Nums] = new ArrayToken(numbers) });
    }

    /// <summary>
    /// A parent tree value in the output: an array of the elements each marked content belongs to, those left out
    /// null in their place; or the one element an object belongs to, unless it was left out.
    /// </summary>
    private static IToken? ParentTreeValue(AssemblyContext context, AssemblySource source, IToken value)
    {
        if (source.Resolve<ArrayToken>(value) is { } array)
        {
            return new ArrayToken(array.Data.Select(item =>
            {
                var copied = context.Copy(source, item);
                return context.IsNull(copied) ? NullToken.Instance : copied;
            }).ToList());
        }

        var element = context.Copy(source, value);
        return context.IsNull(element) ? null : element;
    }

    private DictionaryToken MarkInfo(AssemblyContext context)
    {
        var primary = context.Primary;
        var result = primary.Dictionary(primary.Catalog, NameToken.MarkInfo) is { } markInfo
            ? AssemblyTokens.Mutable((DictionaryToken)context.Copy(primary, markInfo))
            : new Dictionary<NameToken, IToken>();

        result[NameToken.Marked] = BooleanToken.True;

        foreach (var flag in new[] { AssemblyNames.Suspects, AssemblyNames.UserProperties })
        {
            if (trees.Any(tree => tree.Source.Dictionary(tree.Source.Catalog, NameToken.MarkInfo) is { } info
                                  && tree.Source.Resolve<BooleanToken>(info.TryGet(flag, out var f) ? f : null) is { Data: true }))
            {
                result[flag] = BooleanToken.True;
            }
        }

        return new DictionaryToken(result);
    }

    private static IToken? Inherited(SourcePage page, NameToken key)
    {
        if (page.Dictionary.TryGet(key, out var own))
        {
            return own;
        }

        for (var i = page.Ancestors.Count - 1; i >= 0; i--)
        {
            if (page.Ancestors[i].TryGet(key, out var inherited))
            {
                return inherited;
            }
        }

        return null;
    }

    private static IndirectReferenceToken ContentStream(AssemblyContext context, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        var dictionary = new DictionaryToken(new Dictionary<NameToken, IToken> { [NameToken.Length] = new NumericToken(bytes.Length) });
        return context.Writer.WriteToken(new StreamToken(dictionary, bytes));
    }

    /// <summary>
    /// A name as content writes it: every character outside the regular printable ones as <c>#xx</c>.
    /// </summary>
    private static string Escaped(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            var regular = b > 0x20 && b < 0x7F && "#()<>[]{}/%".IndexOf((char)b) < 0;
            builder.Append(regular ? ((char)b).ToString() : "#" + b.ToString("X2"));
        }

        return builder.ToString();
    }

    private static string Unique(string name, Func<string, bool> taken)
    {
        for (var i = 2; ; i++)
        {
            var candidate = name + "_" + i;
            if (!taken(candidate))
            {
                return candidate;
            }
        }
    }

    private sealed class Tree
    {
        public Tree(AssemblySource source, DictionaryToken root)
        {
            Source = source;
            Root = root;
        }

        public AssemblySource Source { get; }

        public DictionaryToken Root { get; }

        /// <summary>
        /// What the source's parent tree keys are moved by.
        /// </summary>
        public int Offset { get; set; }

        public IReadOnlyList<KeyValuePair<int, IToken>> ParentTree { get; set; } = [];

        /// <summary>
        /// The source keys still in use by what is kept.
        /// </summary>
        public HashSet<int> Kept { get; } = new();

        public Dictionary<string, string> Roles { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Classes { get; } = new(StringComparer.Ordinal);

        public List<Kid> Top { get; set; } = new();
    }

    /// <summary>
    /// A structure element's kid kept: its token as the parent lists it, its element when it is one, the earliest
    /// output page of its content, and that of each of its own kids kept.
    /// </summary>
    private sealed class Kid
    {
        public Kid(IToken token, DictionaryToken? element, int? page, IReadOnlyList<int?> kidPages)
        {
            Token = token;
            Element = element;
            Page = page;
            KidPages = kidPages;
        }

        public IToken Token { get; }

        public DictionaryToken? Element { get; }

        public int? Page { get; }

        public IReadOnlyList<int?> KidPages { get; }

        public Kid With(IToken token) => new(token, Element, Page, KidPages);
    }

    /// <summary>
    /// A top element in the output: its reference and earliest page, and how its parent is set.
    /// </summary>
    private sealed class Placed
    {
        public Placed(IndirectReferenceToken output, int? page, Figure? figure)
        {
            Output = output;
            Page = page;
            Figure = figure;
        }

        public IndirectReferenceToken Output { get; }

        public int? Page { get; }

        public Figure? Figure { get; }

        public AssemblySource? Source { get; set; }

        public IndirectReference? Reference { get; set; }

        public IToken? Direct { get; set; }

        public IndirectReferenceToken? Parent { get; set; }
    }

    /// <summary>
    /// The element an untagged page given a tag becomes, and its parent tree key.
    /// </summary>
    private sealed class Figure
    {
        public Figure(AssemblySlot slot, IndirectReferenceToken output, int key)
        {
            Slot = slot;
            Output = output;
            Key = key;
        }

        public AssemblySlot Slot { get; }

        public IndirectReferenceToken Output { get; }

        public int Key { get; }

        public IndirectReferenceToken? Parent { get; set; }
    }

    /// <summary>
    /// How a form a placed page is drawn from is named in the structure: its key in the parent tree, if any, and what its
    /// content is wrapped in, if anything.
    /// </summary>
    public readonly record struct FormMarking(int? Key, string? Before, string? After);
}
