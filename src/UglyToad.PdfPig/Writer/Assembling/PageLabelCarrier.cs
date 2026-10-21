namespace UglyToad.PdfPig.Writer.Assembling;

using System.Collections.Generic;
using System.Linq;
using Parser.Parts;
using Tokenization.Scanner;
using Tokens;

/// <summary>
/// Page labels. Each page kept keeps the label it had — its style, prefix and number — while a page from a document
/// without labels, and each part of a page split, is labelled with its plain page number; the ranges are written again
/// wherever one page's label does not run on from the one before.
/// </summary>
internal sealed class PageLabelCarrier : IAssemblyCarrier
{
    public void Prepare(AssemblyContext context)
    {
    }

    public void Complete(AssemblyContext context, IDictionary<NameToken, IToken> catalog)
    {
        var ranges = new Dictionary<AssemblySource, IReadOnlyList<KeyValuePair<int, IToken>>>();
        foreach (var source in context.Sources)
        {
            if (source.Dictionary(source.Catalog, NameToken.PageLabels) is { } labels)
            {
                ranges[source] = NumberTreeParser.Flatten(labels, source.Scanner);
            }
        }

        if (ranges.Count == 0)
        {
            return;
        }

        var numbers = new List<IToken>();
        Label? previous = null;
        foreach (var slot in context.Slots)
        {
            // A split page's parts are pages its labels never named — in a booklet unfolded, not even next to each other —
            // so each is labelled by where it now is, as a page from a document without labels is.
            var label = !slot.Page.IsSplit && ranges.TryGetValue(slot.Source, out var sourceRanges)
                ? LabelOf(slot, sourceRanges)
                : new Label(NameToken.D, null, slot.Index + 1);

            if (previous is null || !label.RunsOn(previous))
            {
                numbers.Add(new NumericToken(slot.Index));
                numbers.Add(label.Range());
            }

            previous = label;
        }

        catalog[NameToken.PageLabels] = context.Writer.WriteToken(
            new DictionaryToken(new Dictionary<NameToken, IToken> { [NameToken.Nums] = new ArrayToken(numbers) }));
    }

    private static Label LabelOf(AssemblySlot slot, IReadOnlyList<KeyValuePair<int, IToken>> ranges)
    {
        var index = slot.Page.Number - 1;
        var range = ranges.LastOrDefault(entry => entry.Key <= index);
        var scanner = slot.Source.Scanner;
        if (range.Value is null || slot.Source.Resolve<DictionaryToken>(range.Value) is not { } dictionary)
        {
            return new Label(NameToken.D, null, slot.Page.Number);
        }

        var style = AssemblyTokens.Name(dictionary.TryGet(NameToken.S, out var s) ? s : null, scanner);
        var prefix = AssemblyTokens.Text(dictionary.TryGet(NameToken.P, out var p) ? p : null, scanner);
        var start = AssemblyTokens.Integer(dictionary.TryGet(NameToken.St, out var st) ? st : null, scanner) ?? 1;
        return new Label(style, prefix, start + index - range.Key);
    }

    private sealed class Label
    {
        public Label(NameToken? style, string? prefix, int number)
        {
            Style = style;
            Prefix = prefix;
            Number = number;
        }

        public NameToken? Style { get; }

        public string? Prefix { get; }

        public int Number { get; }

        /// <summary>
        /// Whether this label is the one after <paramref name="previous"/> in the same range.
        /// </summary>
        public bool RunsOn(Label previous)
            => Equals(Style, previous.Style) && Prefix == previous.Prefix && Number == previous.Number + 1;

        public DictionaryToken Range()
        {
            var range = new Dictionary<NameToken, IToken> { [NameToken.Type] = AssemblyNames.PageLabel };
            if (Style is not null)
            {
                range[NameToken.S] = Style;
            }

            if (Prefix is not null)
            {
                range[NameToken.P] = new StringToken(Prefix);
            }

            if (Number != 1)
            {
                range[NameToken.St] = new NumericToken(Number);
            }

            return new DictionaryToken(range);
        }
    }
}
