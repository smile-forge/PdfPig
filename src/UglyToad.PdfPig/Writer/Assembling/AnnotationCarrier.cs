namespace UglyToad.PdfPig.Writer.Assembling;

using System;
using System.Collections.Generic;
using System.Linq;
using Core;
using Tokens;

/// <summary>
/// The annotations of the pages kept. A link to a page left out goes with that page; a link through a name only its
/// own document knows is pointed at the page directly; a reply to, or popup of, an annotation left out lets go of it.
/// Every other annotation is copied as the object it is, so whatever else refers to it — a field, a structure element,
/// another annotation — refers to the same one.
/// </summary>
internal sealed class AnnotationCarrier : IAssemblyCarrier
{
    private static readonly NameToken[] AnnotationLinks = [AssemblyNames.InReplyTo, AssemblyNames.Popup, NameToken.Parent];

    private readonly Dictionary<DictionaryToken, DictionaryToken?> direct = new(IdentityComparer<DictionaryToken>.Instance);

    public void Prepare(AssemblyContext context)
    {
        var seen = new HashSet<(AssemblySource, IndirectReference)>();

        foreach (var slot in context.Slots)
        {
            var source = slot.Source;
            foreach (var entry in slot.Page.Annotations)
            {
                var annotation = source.Resolve<DictionaryToken>(entry);
                if (annotation is null)
                {
                    continue;
                }

                if (entry is IndirectReferenceToken reference)
                {
                    if (!seen.Add((source, reference.Data)))
                    {
                        continue;
                    }

                    if (GoesToPageLeftOut(context, source, annotation))
                    {
                        context.Rewrites.Drop(source, reference.Data);
                    }
                    else if (Edit(context, source, annotation) is { } edit)
                    {
                        context.Rewrites.Before(source, reference.Data, edit);
                    }

                    continue;
                }

                direct[annotation] = GoesToPageLeftOut(context, source, annotation)
                    ? null
                    : Edit(context, source, annotation) is { } directEdit ? directEdit(annotation) : annotation;
            }
        }
    }

    public void Complete(AssemblyContext context, IDictionary<NameToken, IToken> catalog)
    {
    }

    /// <summary>
    /// Adds an edit to an annotation written directly in a kept page's <c>/Annots</c>, which has no reference for
    /// another part to claim; it is made when the annotation is written.
    /// </summary>
    public void Adjust(DictionaryToken annotation, Func<DictionaryToken, DictionaryToken> edit)
    {
        if (!direct.TryGetValue(annotation, out var current))
        {
            current = annotation;
        }

        if (current is not null)
        {
            direct[annotation] = edit(current);
        }
    }

    /// <summary>
    /// The annotations written on the page, copied.
    /// </summary>
    public IReadOnlyList<IToken> Written(AssemblyContext context, AssemblySlot slot)
    {
        var written = new List<IToken>(slot.Page.Annotations.Count);
        foreach (var entry in slot.Page.Annotations)
        {
            if (entry is IndirectReferenceToken reference)
            {
                if (context.KeepsAnnotation(slot.Source, reference.Data))
                {
                    written.Add(context.Copy(slot.Source, entry));
                }

                continue;
            }

            if (entry is not DictionaryToken annotation)
            {
                continue;
            }

            if (!direct.TryGetValue(annotation, out var replaced))
            {
                replaced = annotation;
            }

            if (replaced is not null)
            {
                written.Add(context.Copy(slot.Source, replaced));
            }
        }

        return written;
    }

    private static bool GoesToPageLeftOut(AssemblyContext context, AssemblySource source, DictionaryToken annotation)
        => AssemblyTokens.IsName(annotation, NameToken.Subtype, NameToken.Link, source.Scanner)
           && context.IsPageLeftOut(source, Target(context, source, annotation).Page);

    private static ResolvedDestination Target(AssemblyContext context, AssemblySource source, DictionaryToken annotation)
        => annotation.TryGet(NameToken.Dest, out var destination)
            ? context.Destinations.Resolve(source, destination)
            : context.Destinations.ResolveAction(source, annotation.TryGet(NameToken.A, out var action) ? action : null);

    private static Func<DictionaryToken, DictionaryToken>? Edit(AssemblyContext context, AssemblySource source, DictionaryToken annotation)
    {
        var released = AnnotationLinks
            .Where(key => annotation.TryGet(key, out var linked)
                          && linked is IndirectReferenceToken reference
                          && source.PagesHolding(reference.Data) is { Count: > 0 } holding
                          && !holding.Any(page => page.IsKept))
            .ToList();

        ArrayToken? explicitDestination = null;
        if (!source.IsPrimary && AssemblyTokens.IsName(annotation, NameToken.Subtype, NameToken.Link, source.Scanner))
        {
            var target = Target(context, source, annotation);
            if (target.IsNamed && target.Explicit is not null)
            {
                explicitDestination = target.Explicit;
            }
        }

        if (released.Count == 0 && explicitDestination is null)
        {
            return null;
        }

        return dictionary =>
        {
            foreach (var key in released)
            {
                dictionary = dictionary.Without(key);
            }

            if (explicitDestination is null)
            {
                return dictionary;
            }

            if (dictionary.ContainsKey(NameToken.Dest))
            {
                return dictionary.With(NameToken.Dest, explicitDestination);
            }

            return source.Resolve<DictionaryToken>(dictionary.TryGet(NameToken.A, out var action) ? action : null) is { } goTo
                ? dictionary.With(NameToken.A, goTo.With(NameToken.D, explicitDestination))
                : dictionary;
        };
    }
}
