namespace UglyToad.PdfPig.Writer.Assembling;

using System;
using System.Collections.Generic;
using System.Linq;
using Core;
using Parser.Parts;
using Tokenization.Scanner;
using Tokens;

/// <summary>
/// A name tree's entries with their keys as written — a key is a byte string, so it is kept as one — and a tree of
/// one node written back in key order.
/// </summary>
internal static class NameTreeEntries
{
    /// <summary>
    /// Every entry under <paramref name="root"/> in document order, the first of a repeated key kept. Each indirectly
    /// referenced node is visited at most once, so a cyclic <c>/Kids</c> entry ends.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<IDataToken<string>, IToken>> Read(DictionaryToken root, IPdfTokenScanner scanner)
    {
        var result = new List<KeyValuePair<IDataToken<string>, IToken>>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<IndirectReference>();
        var pending = new Stack<DictionaryToken>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var node = pending.Pop();

            if (node.TryGet(NameToken.Names, scanner, out ArrayToken? names))
            {
                for (var i = 0; i + 1 < names.Length; i += 2)
                {
                    if (names[i] is IDataToken<string> key && keys.Add(key.Data))
                    {
                        result.Add(new KeyValuePair<IDataToken<string>, IToken>(key, names[i + 1]));
                    }
                }
            }

            if (!node.TryGet(NameToken.Kids, scanner, out ArrayToken? kids))
            {
                continue;
            }

            for (var i = kids.Length - 1; i >= 0; i--)
            {
                if (kids[i] is IndirectReferenceToken reference && !visited.Add(reference.Data))
                {
                    continue;
                }

                if (DirectObjectFinder.TryGet(kids[i], scanner, out DictionaryToken? kid))
                {
                    pending.Push(kid);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// A name tree of one node holding <paramref name="entries"/>, sorted by the bytes of their keys as the format
    /// requires.
    /// </summary>
    public static DictionaryToken Leaf(IEnumerable<KeyValuePair<IDataToken<string>, IToken>> entries)
    {
        var items = new List<IToken>();
        foreach (var entry in entries.OrderBy(entry => entry.Key, KeyComparer.Instance))
        {
            items.Add((IToken)entry.Key);
            items.Add(entry.Value);
        }

        return new DictionaryToken(new Dictionary<NameToken, IToken> { [NameToken.Names] = new ArrayToken(items) });
    }

    /// <summary>
    /// A key that is <paramref name="key"/> with <paramref name="suffix"/> after it, as a string token.
    /// </summary>
    public static IDataToken<string> Suffixed(IDataToken<string> key, string suffix) => new StringToken(key.Data + suffix);

    private sealed class KeyComparer : IComparer<IDataToken<string>>
    {
        public static readonly KeyComparer Instance = new();

        public int Compare(IDataToken<string>? x, IDataToken<string>? y)
        {
            var left = Bytes(x);
            var right = Bytes(y);
            var length = Math.Min(left.Length, right.Length);
            for (var i = 0; i < length; i++)
            {
                if (left[i] != right[i])
                {
                    return left[i].CompareTo(right[i]);
                }
            }

            return left.Length.CompareTo(right.Length);
        }

        private static byte[] Bytes(IDataToken<string>? key) => key switch
        {
            StringToken text => text.GetBytes(),
            HexToken hex => hex.Bytes.ToArray(),
            null => [],
            _ => System.Text.Encoding.UTF8.GetBytes(key.Data),
        };
    }
}
