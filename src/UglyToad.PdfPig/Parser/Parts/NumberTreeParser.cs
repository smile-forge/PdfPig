namespace UglyToad.PdfPig.Parser.Parts
{
    using System.Collections.Generic;
    using System.Linq;
    using Core;
    using Tokenization.Scanner;
    using Tokens;

    /// <summary>
    /// Reads a number tree — page labels, a structure tree's parent tree — into its entries.
    /// </summary>
    internal static class NumberTreeParser
    {
        /// <summary>
        /// Every entry under <paramref name="root"/>, sorted by key, each value as written: a reference stays a
        /// reference. The first of a repeated key is kept. Each indirectly referenced node is visited at most once, so
        /// a cyclic <c>/Kids</c> entry ends.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<int, IToken>> Flatten(DictionaryToken root, IPdfTokenScanner pdfScanner)
        {
            var result = new Dictionary<int, IToken>();
            var visited = new HashSet<IndirectReference>();
            var pending = new Stack<DictionaryToken>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var node = pending.Pop();

                if (node.TryGet(NameToken.Nums, pdfScanner, out ArrayToken? numbers))
                {
                    for (var i = 0; i + 1 < numbers.Length; i += 2)
                    {
                        if (DirectObjectFinder.TryGet(numbers[i], pdfScanner, out NumericToken? key) && !result.ContainsKey(key.Int))
                        {
                            result[key.Int] = numbers[i + 1];
                        }
                    }
                }

                if (!node.TryGet(NameToken.Kids, pdfScanner, out ArrayToken? kids))
                {
                    continue;
                }

                for (var i = kids.Length - 1; i >= 0; i--)
                {
                    if (kids[i] is IndirectReferenceToken reference && !visited.Add(reference.Data))
                    {
                        continue;
                    }

                    if (DirectObjectFinder.TryGet(kids[i], pdfScanner, out DictionaryToken? kid))
                    {
                        pending.Push(kid);
                    }
                }
            }

            return result.OrderBy(entry => entry.Key).ToList();
        }
    }
}
