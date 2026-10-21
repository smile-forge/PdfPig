namespace UglyToad.PdfPig.Writer.Assembling;

using System;
using System.Collections.Generic;
using System.Linq;
using Core;
using Tokens;

/// <summary>
/// The annotations of the pages kept. A link to a page left out goes with that page; a link to a page split goes to
/// the part its point lies in; a link through a name only its own document knows is pointed at the page directly; a
/// reply to, or popup of, an annotation left out lets go of it. On a split page each annotation goes to the part its
/// centre lies in, a popup with the annotation it belongs to. Every other annotation is copied as the object it is, so
/// whatever else refers to it — a field, a structure element, another annotation — refers to the same one.
/// </summary>
internal sealed class AnnotationCarrier : IAssemblyCarrier
{
    private static readonly NameToken[] AnnotationLinks = [AssemblyNames.InReplyTo, AssemblyNames.Popup, NameToken.Parent];

    private readonly Dictionary<DictionaryToken, DictionaryToken?> direct = new(IdentityComparer<DictionaryToken>.Instance);
    private readonly Dictionary<DictionaryToken, AssemblySlot> directPlaces = new(IdentityComparer<DictionaryToken>.Instance);

    public void Prepare(AssemblyContext context)
    {
        var seen = new HashSet<(AssemblySource, IndirectReference)>();

        foreach (var page in context.Slots.Select(slot => slot.Page).Distinct())
        {
            if (page.IsSplit)
            {
                Place(context, page);
            }
        }

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
                    else if (Edit(context, source, annotation, context.PlacedOn(source, reference.Data)) is { } edit)
                    {
                        context.Rewrites.Before(source, reference.Data, edit);
                    }

                    continue;
                }

                if (direct.ContainsKey(annotation))
                {
                    continue;
                }

                directPlaces.TryGetValue(annotation, out var place);
                direct[annotation] = GoesToPageLeftOut(context, source, annotation)
                    ? null
                    : Edit(context, source, annotation, place) is { } directEdit ? directEdit(annotation) : annotation;
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
    /// The annotations written on the page — on a split page, those on its part — copied.
    /// </summary>
    public IReadOnlyList<IToken> Written(AssemblyContext context, AssemblySlot slot)
    {
        var written = new List<IToken>(slot.Page.Annotations.Count);
        foreach (var entry in slot.Page.Annotations)
        {
            if (entry is IndirectReferenceToken reference)
            {
                if (context.KeepsAnnotation(slot.Source, reference.Data) && context.IsOn(slot, entry))
                {
                    written.Add(context.Copy(slot.Source, entry));
                }

                continue;
            }

            if (entry is not DictionaryToken annotation
                || slot.Page.IsSplit && directPlaces.TryGetValue(annotation, out var place) && place != slot)
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

    /// <summary>
    /// Puts each annotation of a split page on the part its centre lies in — a popup on its annotation's part, wherever
    /// the popup is drawn.
    /// </summary>
    private void Place(AssemblyContext context, SourcePage page)
    {
        var source = page.Slot!.Source;
        var popups = new List<(IToken Entry, DictionaryToken Annotation)>();

        foreach (var entry in page.Annotations)
        {
            if (source.Resolve<DictionaryToken>(entry) is not { } annotation)
            {
                continue;
            }

            if (AssemblyTokens.IsName(annotation, NameToken.Subtype, AssemblyNames.Popup, source.Scanner)
                && annotation.TryGet(NameToken.Parent, out var parent) && parent is IndirectReferenceToken)
            {
                popups.Add((entry, annotation));
                continue;
            }

            Put(context, source, entry, annotation, Rectangle(source, annotation) is { } rect ? page.SlotFor(rect) : page.Slot);
        }

        foreach (var (entry, annotation) in popups)
        {
            var parent = (IndirectReferenceToken)annotation.Data[NameToken.Parent.Data];
            Put(context, source, entry, annotation, context.PlacedOn(source, parent.Data) ?? page.Slot);
        }
    }

    private void Put(AssemblyContext context, AssemblySource source, IToken entry, DictionaryToken annotation, AssemblySlot? slot)
    {
        if (slot is null)
        {
            return;
        }

        if (entry is IndirectReferenceToken reference)
        {
            context.PlaceOn(source, reference.Data, slot);
        }
        else
        {
            directPlaces[annotation] = slot;
        }
    }

    private static PdfRectangle? Rectangle(AssemblySource source, DictionaryToken annotation)
    {
        if (source.Array(annotation, NameToken.Rect) is not { Length: 4 } array)
        {
            return null;
        }

        var n = array.Data.Select(item => source.Resolve<NumericToken>(item)?.Double ?? 0).ToArray();
        return new PdfRectangle(Math.Min(n[0], n[2]), Math.Min(n[1], n[3]), Math.Max(n[0], n[2]), Math.Max(n[1], n[3]));
    }

    private static bool GoesToPageLeftOut(AssemblyContext context, AssemblySource source, DictionaryToken annotation)
        => AssemblyTokens.IsName(annotation, NameToken.Subtype, NameToken.Link, source.Scanner)
           && context.IsPageLeftOut(source, Target(context, source, annotation).Page);

    private static ResolvedDestination Target(AssemblyContext context, AssemblySource source, DictionaryToken annotation)
        => annotation.TryGet(NameToken.Dest, out var destination)
            ? context.Destinations.Resolve(source, destination)
            : context.Destinations.ResolveAction(source, annotation.TryGet(NameToken.A, out var action) ? action : null);

    /// <summary>
    /// What changes about an annotation, in its source's terms: links to annotations left out let go, a link's
    /// destination pointed at its part or made explicit, and the page it says it is on made the part it is put on.
    /// </summary>
    private static Func<DictionaryToken, DictionaryToken>? Edit(
        AssemblyContext context, AssemblySource source, DictionaryToken annotation, AssemblySlot? place)
    {
        var released = AnnotationLinks
            .Where(key => annotation.TryGet(key, out var linked)
                          && linked is IndirectReferenceToken reference
                          && source.PagesHolding(reference.Data) is { Count: > 0 } holding
                          && !holding.Any(page => page.IsKept))
            .ToList();

        IToken? destination = null;
        if (AssemblyTokens.IsName(annotation, NameToken.Subtype, NameToken.Link, source.Scanner))
        {
            var target = Target(context, source, annotation);
            var written = annotation.TryGet(NameToken.Dest, out var dest)
                ? dest
                : source.Resolve<DictionaryToken>(annotation.TryGet(NameToken.A, out var action) ? action : null) is { } goTo
                  && goTo.TryGet(NameToken.D, out var d) ? d : null;

            if (written is not null && target.Explicit is not null)
            {
                var retargeted = context.Retargeted(source, written, target);
                if (!ReferenceEquals(retargeted, written))
                {
                    destination = retargeted;
                }
                else if (target.IsNamed && !source.IsPrimary)
                {
                    destination = target.Explicit;
                }
            }
        }

        var page = place is not null && place != place.Page.Slot ? context.Rewrites.Placeholder(source, place.Reference) : null;

        if (released.Count == 0 && destination is null && page is null)
        {
            return null;
        }

        return dictionary =>
        {
            foreach (var key in released)
            {
                dictionary = dictionary.Without(key);
            }

            if (page is not null)
            {
                dictionary = dictionary.With(NameToken.P, page);
            }

            if (destination is null)
            {
                return dictionary;
            }

            if (dictionary.ContainsKey(NameToken.Dest))
            {
                return dictionary.With(NameToken.Dest, destination);
            }

            return source.Resolve<DictionaryToken>(dictionary.TryGet(NameToken.A, out var action) ? action : null) is { } goTo
                ? dictionary.With(NameToken.A, goTo.With(NameToken.D, destination))
                : dictionary;
        };
    }
}
