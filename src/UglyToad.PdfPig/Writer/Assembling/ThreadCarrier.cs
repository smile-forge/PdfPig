namespace UglyToad.PdfPig.Writer.Assembling;

using System.Collections.Generic;
using System.Linq;
using Core;
using Tokens;

/// <summary>
/// Article threads: each thread keeps the beads on the pages kept, closed again into a ring in the order they ran; a
/// thread with no bead left goes with its pages.
/// </summary>
internal sealed class ThreadCarrier : IAssemblyCarrier
{
    private const int MostBeads = 100_000;

    private readonly List<(AssemblySource Source, IndirectReferenceToken Thread)> threads = new();

    public void Prepare(AssemblyContext context)
    {
        foreach (var source in context.Sources)
        {
            foreach (var entry in source.Array(source.Catalog, NameToken.Threads)?.Data ?? [])
            {
                if (entry is not IndirectReferenceToken thread || source.Resolve<DictionaryToken>(thread) is not { } dictionary)
                {
                    continue;
                }

                var kept = new List<IndirectReference>();
                var parts = new List<AssemblySlot?>();
                foreach (var bead in Ring(source, dictionary))
                {
                    if (!context.Rewrites.IsDropped(source, bead)
                        && source.Resolve<DictionaryToken>(new IndirectReferenceToken(bead)) is { } beadDictionary
                        && beadDictionary.TryGet(NameToken.P, out var page)
                        && page is IndirectReferenceToken pageReference
                        && source.PageAt(pageReference.Data) is { IsKept: true } keptPage)
                    {
                        kept.Add(bead);
                        parts.Add(keptPage.IsSplit || keptPage.IsPlaced ? Part(context, source, bead, beadDictionary, keptPage) : null);
                    }
                    else
                    {
                        context.Rewrites.Drop(source, bead);
                    }
                }

                if (kept.Count == 0)
                {
                    context.Rewrites.Drop(source, thread.Data);
                    continue;
                }

                var threadOutput = context.Rewrites.Claim(source, thread.Data);
                var outputs = new List<IndirectReferenceToken>(kept.Count);
                foreach (var bead in kept)
                {
                    outputs.Add(context.Rewrites.Claim(source, bead));
                }

                var first = outputs[0];
                context.Rewrites.After(source, thread.Data, copied => copied.With(NameToken.F, first));

                for (var i = 0; i < kept.Count; i++)
                {
                    var next = outputs[(i + 1) % outputs.Count];
                    var previous = outputs[(i - 1 + outputs.Count) % outputs.Count];
                    var part = parts[i]?.Reference;

                    // A bead on a page placed on a sheet moves with it.
                    var moved = parts[i]?.ToSheet is { } toSheet
                                && source.Resolve<DictionaryToken>(new IndirectReferenceToken(kept[i])) is { } placedBead
                                && SheetGeometry.Rectangle(source, placedBead.TryGet(NameToken.R, out var r) ? r : null) is { } rectangle
                        ? SheetGeometry.Rectangle(SheetGeometry.Bounds(toSheet, rectangle))
                        : null;
                    context.Rewrites.After(
                        source,
                        kept[i],
                        copied =>
                        {
                            var linked = copied.With(NameToken.N, next).With(NameToken.V, previous).With(NameToken.T, threadOutput);
                            linked = moved is null ? linked : linked.With(NameToken.R, moved);
                            return part is null ? linked : linked.With(NameToken.P, part);
                        });
                }

                threads.Add((source, thread));
            }
        }
    }

    public void Complete(AssemblyContext context, IDictionary<NameToken, IToken> catalog)
    {
        if (threads.Count == 0)
        {
            return;
        }

        var written = new List<IToken>(threads.Count);
        foreach (var (source, thread) in threads)
        {
            written.Add(context.Copy(source, thread));
        }

        catalog[NameToken.Threads] = new ArrayToken(written);
    }

    /// <summary>
    /// The part of a split or placed page a bead lies in — the one holding its rectangle's centre — put on that part.
    /// </summary>
    private static AssemblySlot? Part(AssemblyContext context, AssemblySource source, IndirectReference bead, DictionaryToken dictionary, SourcePage page)
    {
        var slot = page.Slot;
        if (source.Array(dictionary, NameToken.R) is { Length: 4 } rectangle)
        {
            var n = rectangle.Data.Select(item => source.Resolve<NumericToken>(item)?.Double ?? 0).ToArray();
            slot = page.SlotAt((n[0] + n[2]) / 2, (n[1] + n[3]) / 2);
        }

        if (slot is not null)
        {
            context.PlaceOn(source, bead, slot);
        }

        return slot;
    }

    /// <summary>
    /// The thread's beads from its first, following each bead's next until the ring closes.
    /// </summary>
    private static IEnumerable<IndirectReference> Ring(AssemblySource source, DictionaryToken thread)
    {
        if (!thread.TryGet(NameToken.F, out var first) || first is not IndirectReferenceToken current)
        {
            yield break;
        }

        var visited = new HashSet<IndirectReference>();
        while (visited.Count < MostBeads && visited.Add(current.Data))
        {
            yield return current.Data;

            if (source.Resolve<DictionaryToken>(current) is not { } bead
                || !bead.TryGet(NameToken.N, out var next)
                || next is not IndirectReferenceToken nextReference)
            {
                yield break;
            }

            current = nextReference;
        }
    }
}
