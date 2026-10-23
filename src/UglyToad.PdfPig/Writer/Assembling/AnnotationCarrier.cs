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
/// centre lies in, a popup with the annotation it belongs to. On a page placed on a sheet each annotation moves with
/// the page — its rectangle, its points, its appearance turned with it — and where the page is placed more than once,
/// or the annotation is listed on another page placed elsewhere, each further place shows a copy of its own; a form
/// widget is never copied, since a field keeps its widgets. Every other annotation is copied as the object it is, so
/// whatever else refers to it — a field, a structure element, another annotation — refers to the same one.
/// </summary>
internal sealed class AnnotationCarrier : IAssemblyCarrier
{
    private const int NoZoom = 8;
    private const int NoRotate = 16;

    private static readonly NameToken[] AnnotationLinks = [AssemblyNames.InReplyTo, AssemblyNames.Popup, NameToken.Parent];
    private static readonly NameToken[] PointLists = [AssemblyNames.QuadPoints, NameToken.Vertices, NameToken.L, NameToken.Cl];
    private static readonly NameToken[] Appearances = [NameToken.N, NameToken.R, NameToken.D];

    private readonly Dictionary<DictionaryToken, DictionaryToken?> direct = new(IdentityComparer<DictionaryToken>.Instance);
    private readonly Dictionary<DictionaryToken, AssemblySlot> directPlaces = new(IdentityComparer<DictionaryToken>.Instance);
    private readonly Dictionary<(AssemblySlot, DictionaryToken), DictionaryToken?> directOnSheets = new();
    private readonly Dictionary<(AssemblySlot, IndirectReference), IndirectReferenceToken?> copies = new();

    public void Prepare(AssemblyContext context)
    {
        var seen = new HashSet<(AssemblySource, IndirectReference)>();

        foreach (var page in context.Slots.Select(slot => slot.Page).Distinct())
        {
            if (page.IsSplit || page.IsPlaced)
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
                    else if (Edit(context, source, annotation, Owner(context, source, reference.Data), copy: false) is { } edit)
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
                    : Edit(context, source, annotation, place is { IsPlaced: false } ? place : null, copy: false) is { } directEdit
                        ? directEdit(annotation)
                        : annotation;
            }
        }

        foreach (var slot in context.Slots.Where(slot => slot.Page.IsPlaced))
        {
            Copy(context, slot);
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
    /// The annotations written on the page — on a split page, those on its part; on a page placed on a sheet, those it
    /// shows there, moved with it — copied.
    /// </summary>
    public IReadOnlyList<IToken> Written(AssemblyContext context, AssemblySlot slot)
    {
        var written = new List<IToken>(slot.Page.Annotations.Count);
        foreach (var entry in slot.Page.Annotations)
        {
            if (entry is IndirectReferenceToken reference)
            {
                if (copies.TryGetValue((slot, reference.Data), out var copy))
                {
                    if (copy is not null)
                    {
                        written.Add(copy);
                    }

                    continue;
                }

                if (context.KeepsAnnotation(slot.Source, reference.Data) && context.IsOn(slot, entry))
                {
                    written.Add(context.Copy(slot.Source, entry));
                }

                continue;
            }

            if (entry is not DictionaryToken annotation)
            {
                continue;
            }

            DictionaryToken? replaced;
            if (slot.Page.IsPlaced)
            {
                if (!directOnSheets.TryGetValue((slot, annotation), out replaced))
                {
                    continue;
                }
            }
            else
            {
                if (slot.Page.IsSplit && directPlaces.TryGetValue(annotation, out var place) && place != slot)
                {
                    continue;
                }

                if (!direct.TryGetValue(annotation, out replaced))
                {
                    replaced = annotation;
                }
            }

            if (replaced is not null)
            {
                written.Add(context.Copy(slot.Source, replaced));
            }
        }

        return written;
    }

    /// <summary>
    /// Puts each annotation of a split or placed page on the part its centre lies in — a popup on its annotation's
    /// part, wherever the popup is drawn.
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

            if (IsPopup(source, annotation) && annotation.TryGet(NameToken.Parent, out var parent) && parent is IndirectReferenceToken)
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

    /// <summary>
    /// The place an annotation is written at as the object it is: the part it was put on, or the first place of the
    /// first page listing it.
    /// </summary>
    private static AssemblySlot? Owner(AssemblyContext context, AssemblySource source, IndirectReference annotation)
        => context.PlacedOn(source, annotation) ?? source.PagesHolding(annotation).Select(page => page.Slot).FirstOrDefault(slot => slot is not null);

    /// <summary>
    /// Decides what each annotation listed on a page placed on a sheet becomes there: the object itself where it is
    /// written as itself; a copy of its own, moved with this placement, where it shows here too — a popup copied with
    /// the annotation it belongs to; or nothing, for a form widget shown elsewhere, or an annotation that lies in
    /// another part.
    /// </summary>
    private void Copy(AssemblyContext context, AssemblySlot slot)
    {
        var source = slot.Source;
        foreach (var entry in slot.Page.Annotations)
        {
            if (source.Resolve<DictionaryToken>(entry) is not { } annotation)
            {
                continue;
            }

            if (entry is DictionaryToken written)
            {
                directOnSheets[(slot, written)] = Shows(slot, source, annotation, directPlaces.TryGetValue(written, out var place) ? place : null)
                    && !(IsWidget(source, annotation) && place is not null && place != slot)
                    && direct.TryGetValue(written, out var kept) && kept is not null
                        ? Edit(context, source, kept, slot, copy: false) is { } edit ? edit(kept) : kept
                        : null;
                continue;
            }

            var reference = ((IndirectReferenceToken)entry).Data;
            if (!context.KeepsAnnotation(source, reference) || copies.ContainsKey((slot, reference)) || IsPopup(source, annotation))
            {
                continue;
            }

            var owner = Owner(context, source, reference);
            if (owner is null || owner == slot || !Shows(slot, source, annotation, context.PlacedOn(source, reference)))
            {
                continue;
            }

            if (IsWidget(source, annotation))
            {
                copies[(slot, reference)] = null;
                continue;
            }

            var popup = annotation.TryGet(AssemblyNames.Popup, out var popupToken) ? popupToken as IndirectReferenceToken : null;
            var copyHandle = (IndirectReferenceToken?)null;
            var popupHandle = (IndirectReferenceToken?)null;

            var copyEdit = Edit(context, source, annotation, slot, copy: true)!;
            var copyOutput = context.Rewrites.Detached(source, reference, dictionary =>
            {
                dictionary = copyEdit(dictionary);
                return popupHandle is null ? dictionary.Without(AssemblyNames.Popup) : dictionary.With(AssemblyNames.Popup, popupHandle);
            });
            copyHandle = context.Rewrites.Placeholder(source, copyOutput);
            copies[(slot, reference)] = copyOutput;

            if (popup is not null
                && context.KeepsAnnotation(source, popup.Data)
                && source.Resolve<DictionaryToken>(popup) is { } popupAnnotation)
            {
                var popupEdit = Edit(context, source, popupAnnotation, slot, copy: true)!;
                var popupOutput = context.Rewrites.Detached(source, popup.Data, dictionary => popupEdit(dictionary).With(NameToken.Parent, copyHandle));
                popupHandle = context.Rewrites.Placeholder(source, popupOutput);
                copies[(slot, popup.Data)] = popupOutput;
            }
        }

        // A popup of an annotation not shown here, nor written here as itself, is not shown here either.
        foreach (var entry in slot.Page.Annotations)
        {
            if (entry is IndirectReferenceToken reference
                && !copies.ContainsKey((slot, reference.Data))
                && source.Resolve<DictionaryToken>(entry) is { } annotation
                && IsPopup(source, annotation)
                && Owner(context, source, reference.Data) is { } owner && owner != slot)
            {
                copies[(slot, reference.Data)] = null;
            }
        }
    }

    /// <summary>
    /// Whether a placement shows an annotation of its page: on a page shown once, always; on one shown in more than one
    /// place, where the place holds the annotation's centre — every twin of a placement does — or, with no rectangle,
    /// where the annotation was put.
    /// </summary>
    private static bool Shows(AssemblySlot slot, AssemblySource source, DictionaryToken annotation, AssemblySlot? put)
    {
        if (!slot.Page.IsSplit)
        {
            return true;
        }

        return Rectangle(source, annotation) is { } rect ? slot.Page.SlotsShowing(rect).Contains(slot) : put == slot;
    }

    private static PdfRectangle? Rectangle(AssemblySource source, DictionaryToken annotation)
        => SheetGeometry.Rectangle(source, annotation.TryGet(NameToken.Rect, out var rect) ? rect : null);

    private static bool IsPopup(AssemblySource source, DictionaryToken annotation)
        => AssemblyTokens.IsName(annotation, NameToken.Subtype, AssemblyNames.Popup, source.Scanner);

    private static bool IsWidget(AssemblySource source, DictionaryToken annotation)
        => AssemblyTokens.IsName(annotation, NameToken.Subtype, NameToken.Widget, source.Scanner);

    private static bool GoesToPageLeftOut(AssemblyContext context, AssemblySource source, DictionaryToken annotation)
        => AssemblyTokens.IsName(annotation, NameToken.Subtype, NameToken.Link, source.Scanner)
           && context.IsPageLeftOut(source, Target(context, source, annotation).Page);

    private static ResolvedDestination Target(AssemblyContext context, AssemblySource source, DictionaryToken annotation)
        => annotation.TryGet(NameToken.Dest, out var destination)
            ? context.Destinations.Resolve(source, destination)
            : context.Destinations.ResolveAction(source, annotation.TryGet(NameToken.A, out var action) ? action : null);

    /// <summary>
    /// What changes about an annotation, in its source's terms: links to annotations left out let go, a link's
    /// destination pointed at its part or made explicit, the page it says it is on made the part it is put on, and on
    /// a page placed on a sheet, its geometry moved there. A <paramref name="copy"/> for a further place says it is on
    /// that place, and leaves the structure and the reply it belonged to with the original.
    /// </summary>
    private static Func<DictionaryToken, DictionaryToken>? Edit(
        AssemblyContext context, AssemblySource source, DictionaryToken annotation, AssemblySlot? place, bool copy)
    {
        var released = AnnotationLinks
            .Where(key => annotation.TryGet(key, out var linked)
                          && linked is IndirectReferenceToken reference
                          && source.PagesHolding(reference.Data) is { Count: > 0 } holding
                          && !holding.Any(page => page.IsKept))
            .ToList();

        if (copy)
        {
            released.Add(AssemblyNames.InReplyTo);
            released.Add(NameToken.StructParent);
        }

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

        var page = place is not null && (copy || place != place.Page.Slot) ? context.Rewrites.Placeholder(source, place.Reference) : null;
        var moved = place?.ToSheet is { } toSheet ? Moved(context, source, annotation, place, toSheet) : null;

        if (released.Count == 0 && destination is null && page is null && moved is null)
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

            if (moved is not null)
            {
                dictionary = moved(dictionary);
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

    /// <summary>
    /// An annotation's geometry moved to where its page is placed: its rectangle and the points it is drawn through,
    /// its insets, and — across a turn — its appearance turned with it, a widget saying so. One that keeps upright, or
    /// keeps its size, as the page turns or is zoomed keeps its appearance and stays pinned where its top left corner,
    /// as the page shows it, now is.
    /// </summary>
    private static Func<DictionaryToken, DictionaryToken>? Moved(
        AssemblyContext context, AssemblySource source, DictionaryToken annotation, AssemblySlot place, TransformationMatrix toSheet)
    {
        if (Rectangle(source, annotation) is not { } rect)
        {
            return null;
        }

        var flags = AssemblyTokens.Integer(annotation.TryGet(NameToken.F, out var f) ? f : null, source.Scanner) ?? 0;
        var keepsUpright = (flags & NoRotate) != 0;
        var keepsSize = (flags & NoZoom) != 0;
        var turn = SheetGeometry.Turn(toSheet);

        PdfRectangle movedRect;
        if (keepsUpright || keepsSize)
        {
            var corner = toSheet.Transform(TopLeftAsShown(source, place.Page, rect));
            var scale = keepsSize ? 1 : SheetGeometry.Scale(toSheet);
            var swapped = !keepsUpright && SheetGeometry.SwapsAxes(toSheet);
            var width = (swapped ? rect.Height : rect.Width) * scale;
            var height = (swapped ? rect.Width : rect.Height) * scale;
            movedRect = new PdfRectangle(corner.X, corner.Y - height, corner.X + width, corner.Y);
        }
        else
        {
            movedRect = SheetGeometry.Bounds(toSheet, rect);
        }

        var lists = new Dictionary<NameToken, IToken>();
        foreach (var key in PointLists)
        {
            if (SheetGeometry.Points(source, annotation.TryGet(key, out var points) ? points : null, toSheet) is { } movedPoints)
            {
                lists[key] = movedPoints;
            }
        }

        if (source.Array(annotation, AssemblyNames.InkList) is { } ink)
        {
            var strokes = ink.Data.Select(stroke => (IToken?)SheetGeometry.Points(source, stroke, toSheet)).ToList();
            if (strokes.All(stroke => stroke is not null))
            {
                lists[AssemblyNames.InkList] = new ArrayToken(strokes!);
            }
        }

        var insets = keepsUpright || keepsSize
            ? null
            : SheetGeometry.Insets(source, annotation.TryGet(NameToken.Rd, out var rd) ? rd : null, rect, toSheet);

        IToken? appearance = null;
        IToken? characteristics = null;
        if (turn != 0 && !keepsUpright)
        {
            appearance = Turned(context, source, annotation, SheetGeometry.Turning(toSheet));
            if (source.Dictionary(annotation, NameToken.Mk) is { } mk)
            {
                var rotation = AssemblyTokens.Integer(mk.TryGet(NameToken.R, out var r) ? r : null, source.Scanner) ?? 0;
                characteristics = mk.With(NameToken.R, new NumericToken(((rotation + turn) % 360 + 360) % 360));
            }
        }

        return dictionary =>
        {
            dictionary = dictionary.With(NameToken.Rect, SheetGeometry.Rectangle(movedRect));
            foreach (var list in lists)
            {
                dictionary = dictionary.With(list.Key, list.Value);
            }

            if (insets is not null)
            {
                dictionary = dictionary.With(NameToken.Rd, insets);
            }

            if (appearance is not null)
            {
                dictionary = dictionary.With(NameToken.Ap, appearance);
            }

            if (characteristics is not null)
            {
                dictionary = dictionary.With(NameToken.Mk, characteristics);
            }

            return dictionary;
        };
    }

    /// <summary>
    /// The corner of a rectangle of the page that is its top left as the page is shown, turned by its rotation.
    /// </summary>
    private static PdfPoint TopLeftAsShown(AssemblySource source, SourcePage page, PdfRectangle rect)
    {
        var rotation = AssemblyTokens.Integer(page.Inherited(NameToken.Rotate), source.Scanner) ?? 0;
        return ((rotation % 360 + 360) % 360) switch
        {
            90  => new PdfPoint(rect.Left, rect.Bottom),
            180 => new PdfPoint(rect.Right, rect.Bottom),
            270 => new PdfPoint(rect.Right, rect.Top),
            _   => new PdfPoint(rect.Left, rect.Top),
        };
    }

    /// <summary>
    /// An annotation's appearances turned by <paramref name="turning"/>: each appearance stream written again as a copy of
    /// its own whose matrix turns it — the fit to the annotation's moved rectangle then sizes it — so the original,
    /// which another annotation or placement may draw, stays as it was.
    /// </summary>
    private static IToken? Turned(AssemblyContext context, AssemblySource source, DictionaryToken annotation, TransformationMatrix turning)
    {
        if (source.Dictionary(annotation, NameToken.Ap) is not { } appearances)
        {
            return null;
        }

        IToken TurnedStream(IToken token)
        {
            if (token is not IndirectReferenceToken reference || source.Resolve<StreamToken>(token) is not { } stream)
            {
                return token;
            }

            var matrix = SheetGeometry.Matrix(source, stream.StreamDictionary.TryGet(NameToken.Matrix, out var m) ? m : null)
                         ?? TransformationMatrix.Identity;
            var turned = SheetGeometry.Matrix(matrix.Multiply(turning));
            var copy = context.Rewrites.Detached(source, reference.Data, dictionary => dictionary.With(NameToken.Matrix, turned));
            return context.Rewrites.Placeholder(source, copy);
        }

        var result = new Dictionary<NameToken, IToken>();
        foreach (var entry in appearances.Data)
        {
            var key = NameToken.Create(entry.Key);
            if (!Appearances.Contains(key))
            {
                result[key] = entry.Value;
                continue;
            }

            if (source.Resolve<StreamToken>(entry.Value) is not null)
            {
                result[key] = TurnedStream(entry.Value);
            }
            else if (source.Resolve<DictionaryToken>(entry.Value) is { } states)
            {
                result[key] = new DictionaryToken(states.Data.ToDictionary(state => NameToken.Create(state.Key), state => TurnedStream(state.Value)));
            }
            else
            {
                result[key] = entry.Value;
            }
        }

        return new DictionaryToken(result);
    }
}
