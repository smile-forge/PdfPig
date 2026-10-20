namespace UglyToad.PdfPig.Writer.Assembling;

using System;
using System.Collections.Generic;
using System.Linq;
using Parser.Parts;
using Tokenization.Scanner;
using Tokens;

/// <summary>
/// Reading source tokens the way every part of an assembly needs them, and ordering what it carries by page.
/// </summary>
internal static class AssemblyTokens
{
    /// <summary>
    /// The items of an entry that may be one item or an array of them (a structure element's <c>/K</c>, a page's
    /// <c>/Contents</c>): an array's items, one item alone, or none.
    /// </summary>
    public static IReadOnlyList<IToken> Items(IToken? token, IPdfTokenScanner scanner)
    {
        if (token is null || token is NullToken)
        {
            return [];
        }

        if (token is ArrayToken array)
        {
            return array.Data;
        }

        if (token is IndirectReferenceToken reference
            && DirectObjectFinder.Get<IToken>(reference.Data, scanner) is ArrayToken referenced)
        {
            return referenced.Data;
        }

        return [token];
    }

    public static Dictionary<NameToken, IToken> Mutable(DictionaryToken dictionary)
    {
        var result = new Dictionary<NameToken, IToken>(dictionary.Data.Count);
        foreach (var entry in dictionary.Data)
        {
            result[NameToken.Create(entry.Key)] = entry.Value;
        }

        return result;
    }

    public static string? Text(IToken? token, IPdfTokenScanner scanner)
        => token is not null && DirectObjectFinder.TryGet(token, scanner, out IToken? resolved) && resolved is IDataToken<string> text
            ? text.Data
            : null;

    public static int? Integer(IToken? token, IPdfTokenScanner scanner)
        => token is not null && DirectObjectFinder.TryGet(token, scanner, out NumericToken? number) ? number.Int : null;

    public static NameToken? Name(IToken? token, IPdfTokenScanner scanner)
        => token is not null && DirectObjectFinder.TryGet(token, scanner, out NameToken? name) ? name : null;

    /// <summary>
    /// Whether <paramref name="dictionary"/>'s <paramref name="key"/> is the name <paramref name="expected"/>.
    /// </summary>
    public static bool IsName(DictionaryToken dictionary, NameToken key, NameToken expected, IPdfTokenScanner scanner)
        => dictionary.TryGet(key, out var token) && Name(token, scanner) is { } name && name.Equals(expected);

    /// <summary>
    /// <paramref name="items"/> in the order their pages now have, keeping the order of items on the same page; an
    /// item on no page stays after the one before it.
    /// </summary>
    public static IReadOnlyList<T> ByPage<T>(IReadOnlyList<T> items, Func<T, int?> page)
    {
        var keys = new int[items.Count];
        var last = int.MinValue;
        for (var i = 0; i < items.Count; i++)
        {
            last = page(items[i]) ?? last;
            keys[i] = last;
        }

        return items.Select((item, index) => (item, index)).OrderBy(x => keys[x.index]).Select(x => x.item).ToList();
    }

    /// <summary>
    /// <paramref name="kept"/>, with each of <paramref name="inserted"/> placed before the first item whose page comes
    /// after its own, and those on no page at the end.
    /// </summary>
    public static List<T> Interleave<T>(IReadOnlyList<T> kept, IEnumerable<T> inserted, Func<T, int?> page)
    {
        var result = kept.ToList();
        foreach (var item in ByPage(inserted.ToList(), page))
        {
            var at = page(item);
            var index = result.Count;
            if (at.HasValue)
            {
                for (var i = 0; i < result.Count; i++)
                {
                    if (page(result[i]) > at)
                    {
                        index = i;
                        break;
                    }
                }
            }

            result.Insert(index, item);
        }

        return result;
    }

    /// <summary>
    /// The lowest of two pages, where null is no page.
    /// </summary>
    public static int? Earliest(int? first, int? second)
        => first is null ? second : second is null ? first : Math.Min(first.Value, second.Value);
}
