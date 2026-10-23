namespace UglyToad.PdfPig.Writer.Assembling;

using System;
using System.Collections.Generic;
using Core;
using Parser.Parts;
using Tokens;

/// <summary>
/// The source objects an assembly writes changed or leaves out. Each is claimed — given its output reference — before
/// any page is copied, so every reference to it, from wherever it is reached, lands on the one object written with
/// all its edits; and every part that edits it adds to the same edits rather than writing a copy of its own.
/// </summary>
internal sealed class RewrittenObjects
{
    private readonly AssemblyContext context;
    private readonly List<Rewrite> claimed = new();
    private readonly Dictionary<AssemblySource, Dictionary<IndirectReference, Rewrite>> bySource = new();
    private readonly Dictionary<(AssemblySource, IndirectReference), IndirectReferenceToken> placeholders = new();
    private long nextPlaceholder = -1;
    private bool written;

    public RewrittenObjects(AssemblyContext context)
    {
        this.context = context;
    }

    /// <summary>
    /// Reserves the object's output reference, so references to it can be written before it is.
    /// </summary>
    public IndirectReferenceToken Claim(AssemblySource source, IndirectReference reference) => Claimed(source, reference).Output;

    /// <summary>
    /// Changes the object in its source's terms, before it is copied: what the edit leaves in is copied as any
    /// source token is.
    /// </summary>
    public IndirectReferenceToken Before(AssemblySource source, IndirectReference reference, Func<DictionaryToken, DictionaryToken> edit)
    {
        var rewrite = Claimed(source, reference);
        rewrite.Before.Add(edit);
        return rewrite.Output;
    }

    /// <summary>
    /// Changes the object once copied, in the output's terms: what the edit puts in is written as it is.
    /// </summary>
    public IndirectReferenceToken After(AssemblySource source, IndirectReference reference, Func<DictionaryToken, DictionaryToken> edit)
    {
        var rewrite = Claimed(source, reference);
        rewrite.After.Add(edit);
        return rewrite.Output;
    }

    /// <summary>
    /// A copy of the object, written at a reference of its own with <paramref name="edit"/> made in its source's terms, for
    /// a place that needs it changed while every other keeps the original — an annotation on a page placed twice, an
    /// appearance turned for one placement. Every reference to the original still lands on the original.
    /// </summary>
    public IndirectReferenceToken Detached(AssemblySource source, IndirectReference reference, Func<DictionaryToken, DictionaryToken> edit)
    {
        if (written)
        {
            throw new InvalidOperationException("Objects are copied before the assembly writes them.");
        }

        var rewrite = new Rewrite(source, reference, context.Writer.ReserveObjectNumber());
        rewrite.Before.Add(edit);
        claimed.Add(rewrite);
        return rewrite.Output;
    }

    /// <summary>
    /// Edits an object as its owner lists it: an indirect one through its claim, so the token stays its reference;
    /// a direct dictionary in place, so the token is the edited dictionary.
    /// </summary>
    public IToken Apply(AssemblySource source, IToken token, IReadOnlyList<Func<DictionaryToken, DictionaryToken>> edits)
    {
        if (edits.Count == 0)
        {
            return token;
        }

        if (token is IndirectReferenceToken reference)
        {
            foreach (var edit in edits)
            {
                Before(source, reference.Data, edit);
            }

            return token;
        }

        var dictionary = (DictionaryToken)token;
        foreach (var edit in edits)
        {
            dictionary = edit(dictionary);
        }

        return dictionary;
    }

    /// <summary>
    /// Points every reference to a source object at an object the assembly writes itself, such as a source's
    /// structure tree root at the assembled one.
    /// </summary>
    public void Redirect(AssemblySource source, IndirectReference reference, IndirectReferenceToken output)
    {
        if (source.References.ContainsKey(reference))
        {
            throw new InvalidOperationException($"Object {reference} was written, or left out, before it was redirected.");
        }

        source.References.Add(reference, output);
    }

    /// <summary>
    /// A reference a source token can hold to stand for an object the assembly writes itself — one part of a split
    /// page — so an edit in the source's terms can point at it: copied, it becomes <paramref name="output"/>. Its
    /// object number is negative, which no source object has.
    /// </summary>
    public IndirectReferenceToken Placeholder(AssemblySource source, IndirectReferenceToken output)
    {
        if (placeholders.TryGetValue((source, output.Data), out var known))
        {
            return known;
        }

        var handle = new IndirectReferenceToken(new IndirectReference(nextPlaceholder--, 0));
        source.References.Add(handle.Data, output);
        placeholders[(source, output.Data)] = handle;
        return handle;
    }

    /// <summary>
    /// Leaves the object out: every reference to it becomes the null object.
    /// </summary>
    public void Drop(AssemblySource source, IndirectReference reference)
    {
        if (bySource.TryGetValue(source, out var rewrites) && rewrites.TryGetValue(reference, out var rewrite))
        {
            rewrite.Dropped = true;
            source.References[reference] = context.NullReference;
            return;
        }

        if (source.References.TryGetValue(reference, out var existing))
        {
            if (context.IsNull(existing))
            {
                return;
            }

            throw new InvalidOperationException($"Object {reference} was written before it was left out.");
        }

        source.References.Add(reference, context.NullReference);
    }

    public bool IsDropped(AssemblySource source, IndirectReference reference)
        => source.References.TryGetValue(reference, out var output) && context.IsNull(output);

    /// <summary>
    /// Writes every object claimed: the null object at a claim later left out, else the source object with its edits.
    /// </summary>
    public void WriteAll()
    {
        written = true;

        foreach (var rewrite in claimed)
        {
            context.ThrowIfCancelled();

            if (rewrite.Dropped)
            {
                context.Writer.WriteToken(NullToken.Instance, rewrite.Output);
                continue;
            }

            var token = DirectObjectFinder.Get<IToken>(rewrite.Reference, rewrite.Source.Scanner);
            context.Writer.WriteToken(Rewritten(rewrite, token), rewrite.Output);
        }
    }

    private Rewrite Claimed(AssemblySource source, IndirectReference reference)
    {
        if (written)
        {
            throw new InvalidOperationException("Objects are claimed before the assembly writes them.");
        }

        if (!bySource.TryGetValue(source, out var rewrites))
        {
            rewrites = new Dictionary<IndirectReference, Rewrite>();
            bySource[source] = rewrites;
        }

        if (rewrites.TryGetValue(reference, out var existing))
        {
            if (existing.Dropped)
            {
                throw new InvalidOperationException($"Object {reference} was left out before it was claimed.");
            }

            return existing;
        }

        if (source.References.ContainsKey(reference))
        {
            throw new InvalidOperationException($"Object {reference} was written, or left out, before it was claimed.");
        }

        var rewrite = new Rewrite(source, reference, context.Writer.ReserveObjectNumber());
        rewrites.Add(reference, rewrite);
        claimed.Add(rewrite);
        source.References.Add(reference, rewrite.Output);
        return rewrite;
    }

    private IToken Rewritten(Rewrite rewrite, IToken? token)
    {
        switch (token)
        {
            case DictionaryToken dictionary:
                return Edited(rewrite, dictionary);
            case StreamToken stream:
                return new StreamToken(Edited(rewrite, stream.StreamDictionary), stream.Data);
            case null:
                return NullToken.Instance;
            default:
                if (rewrite.Before.Count > 0 || rewrite.After.Count > 0)
                {
                    throw new InvalidOperationException($"Object {rewrite.Reference} is not a dictionary to edit.");
                }

                return context.Copy(rewrite.Source, token);
        }
    }

    private DictionaryToken Edited(Rewrite rewrite, DictionaryToken dictionary)
    {
        foreach (var edit in rewrite.Before)
        {
            dictionary = edit(dictionary);
        }

        var copied = (DictionaryToken)context.Copy(rewrite.Source, dictionary);

        foreach (var edit in rewrite.After)
        {
            copied = edit(copied);
        }

        return copied;
    }

    private sealed class Rewrite
    {
        public Rewrite(AssemblySource source, IndirectReference reference, IndirectReferenceToken output)
        {
            Source = source;
            Reference = reference;
            Output = output;
        }

        public AssemblySource Source { get; }

        public IndirectReference Reference { get; }

        public IndirectReferenceToken Output { get; }

        public List<Func<DictionaryToken, DictionaryToken>> Before { get; } = new();

        public List<Func<DictionaryToken, DictionaryToken>> After { get; } = new();

        public bool Dropped { get; set; }
    }
}
