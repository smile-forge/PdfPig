namespace UglyToad.PdfPig.Writer.Assembling;

using System;
using System.Collections.Generic;
using System.Linq;
using Tokens;

/// <summary>
/// The name trees — named destinations, embedded files, document scripts, named pages, templates and the rest — and
/// the older dictionary of named destinations. Each entry is carried unless what it names is left out; the primary's
/// entries keep their names, and a later source's entry whose name is taken is renamed apart.
/// </summary>
internal sealed class NameTreeCarrier : IAssemblyCarrier
{
    public void Prepare(AssemblyContext context)
    {
    }

    public void Complete(AssemblyContext context, IDictionary<NameToken, IToken> catalog)
    {
        var trees = new Dictionary<string, Category>(StringComparer.Ordinal);
        var dests = new Dictionary<NameToken, IToken>();

        foreach (var source in context.Sources)
        {
            context.ThrowIfCancelled();

            if (source.Dictionary(source.Catalog, NameToken.Names) is { } names)
            {
                foreach (var tree in names.Data)
                {
                    if (source.Resolve<DictionaryToken>(tree.Value) is not { } root)
                    {
                        continue;
                    }

                    if (!trees.TryGetValue(tree.Key, out var category))
                    {
                        category = new Category();
                        trees[tree.Key] = category;
                    }

                    var destinations = tree.Key == NameToken.Dests.Data;
                    foreach (var entry in NameTreeEntries.Read(root, source.Scanner))
                    {
                        if (Carried(context, source, entry.Value, destinations) is { } value)
                        {
                            category.Add(entry.Key, value);
                        }
                    }
                }
            }

            if (source.Dictionary(source.Catalog, NameToken.Dests) is { } legacy)
            {
                foreach (var entry in legacy.Data)
                {
                    var name = NameToken.Create(entry.Key);
                    if (!dests.ContainsKey(name) && Carried(context, source, entry.Value, destinations: true) is { } value)
                    {
                        dests[name] = value;
                    }
                }
            }
        }

        var written = trees
            .Where(tree => tree.Value.Entries.Count > 0)
            .ToDictionary(tree => NameToken.Create(tree.Key), tree => (IToken)context.Writer.WriteToken(NameTreeEntries.Leaf(tree.Value.Entries)));
        if (written.Count > 0)
        {
            catalog[NameToken.Names] = new DictionaryToken(written);
        }

        if (dests.Count > 0)
        {
            catalog[NameToken.Dests] = context.Writer.WriteToken(new DictionaryToken(dests));
        }
    }

    /// <summary>
    /// The entry's value in the output, or null when what it names is left out: a destination to a page left out, or
    /// a value that was itself left out.
    /// </summary>
    private static IToken? Carried(AssemblyContext context, AssemblySource source, IToken value, bool destinations)
    {
        if (destinations && context.IsPageLeftOut(source, context.Destinations.Resolve(source, value).Page))
        {
            return null;
        }

        var copied = context.Copy(source, value);
        return context.IsNull(copied) ? null : copied;
    }

    private sealed class Category
    {
        private readonly HashSet<string> names = new(StringComparer.Ordinal);

        public List<KeyValuePair<IDataToken<string>, IToken>> Entries { get; } = new();

        public void Add(IDataToken<string> key, IToken value)
        {
            if (!names.Add(key.Data))
            {
                var suffix = 2;
                while (!names.Add(key.Data + "_" + suffix))
                {
                    suffix++;
                }

                key = NameTreeEntries.Suffixed(key, "_" + suffix);
            }

            Entries.Add(new KeyValuePair<IDataToken<string>, IToken>(key, value));
        }
    }
}
