namespace UglyToad.PdfPig.Writer.Assembling;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Core;
using Tokens;

/// <summary>
/// The interactive form. A field keeps the widgets on the pages kept and goes when none is left, unless it never had
/// one; fields from several documents share one form, a root field whose name is taken renamed apart, and a default
/// resource whose name is taken renamed apart with every default appearance that names it.
/// </summary>
internal sealed class AcroFormCarrier : IAssemblyCarrier
{
    private const int DeepestField = 64;

    private static readonly NameToken[] Merged =
    [
        NameToken.Fields, NameToken.Co, NameToken.Dr, NameToken.Da, NameToken.Q, NameToken.SigFlags, NameToken.NeedAppearances,
    ];

    private static readonly Regex ResourceName = new(@"/([^\s/\[\]()<>{}%]+)", RegexOptions.CultureInvariant);

    private readonly List<Form> forms = new();

    public void Prepare(AssemblyContext context)
    {
        var rootNames = new HashSet<string>(StringComparer.Ordinal);
        var resourceNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        string? defaultAppearance = null;
        int? quadding = null;

        foreach (var source in context.Sources)
        {
            if (source.Dictionary(source.Catalog, NameToken.AcroForm) is not { } dictionary)
            {
                continue;
            }

            var form = new Form(source, dictionary);
            forms.Add(form);

            RenameResources(form, resourceNames);

            var formAppearance = AssemblyTokens.Text(dictionary.TryGet(NameToken.Da, out var da) ? da : null, source.Scanner);
            var formQuadding = AssemblyTokens.Integer(dictionary.TryGet(NameToken.Q, out var q) ? q : null, source.Scanner);
            if (forms.Count == 1)
            {
                defaultAppearance = formAppearance;
                quadding = formQuadding;
            }

            var visited = new HashSet<IndirectReference>();
            foreach (var root in source.Array(dictionary, NameToken.Fields)?.Data ?? [])
            {
                var field = Visit(context, form, root, 0, visited);
                if (field is not { Kept: true })
                {
                    continue;
                }

                var rootEdits = new List<Func<DictionaryToken, DictionaryToken>>();
                var name = AssemblyTokens.Text(field.Dictionary.TryGet(NameToken.T, out var t) ? t : null, source.Scanner);
                if (name is not null && !rootNames.Add(name))
                {
                    var renamed = Unique(name, rootNames);
                    rootEdits.Add(d => d.With(NameToken.T, new StringToken(renamed)));
                }

                if (forms.Count > 1)
                {
                    if (formAppearance is not null && formAppearance != defaultAppearance && !field.Dictionary.ContainsKey(NameToken.Da))
                    {
                        var appearance = form.Renamed(formAppearance);
                        rootEdits.Add(d => d.With(NameToken.Da, new StringToken(appearance)));
                    }

                    if (formQuadding.HasValue && formQuadding != quadding && !field.Dictionary.ContainsKey(NameToken.Q))
                    {
                        var value = formQuadding.Value;
                        rootEdits.Add(d => d.With(NameToken.Q, new NumericToken(value)));
                    }
                }

                form.Roots.Add(context.Rewrites.Apply(source, field.Token, rootEdits));
            }
        }
    }

    public void Complete(AssemblyContext context, IDictionary<NameToken, IToken> catalog)
    {
        if (forms.Count == 0)
        {
            return;
        }

        var first = forms[0];
        var result = new Dictionary<NameToken, IToken>();
        foreach (var entry in first.Dictionary.Data)
        {
            var name = NameToken.Create(entry.Key);
            if (!Merged.Contains(name) && !name.Equals(NameToken.Xfa))
            {
                result[name] = context.Copy(first.Source, entry.Value);
            }
        }

        result[NameToken.Fields] = new ArrayToken(
            forms.SelectMany(form => form.Roots.Select(root => context.Copy(form.Source, root))).Where(field => !context.IsNull(field)).ToList());

        var calculationOrder = forms
            .SelectMany(form => (form.Source.Array(form.Dictionary, NameToken.Co)?.Data ?? []).Select(field => context.Copy(form.Source, field)))
            .Where(field => !context.IsNull(field))
            .ToList();
        if (calculationOrder.Count > 0)
        {
            result[NameToken.Co] = new ArrayToken(calculationOrder);
        }

        var resources = Resources(context);
        if (resources.Count > 0)
        {
            result[NameToken.Dr] = new DictionaryToken(resources);
        }

        foreach (var key in new[] { NameToken.Da, NameToken.Q })
        {
            if (first.Dictionary.TryGet(key, out var value))
            {
                result[key] = context.Copy(first.Source, value);
            }
        }

        var signatureFlags = forms.Aggregate(0, (flags, form) => flags | (AssemblyTokens.Integer(form.Dictionary.TryGet(NameToken.SigFlags, out var f) ? f : null, form.Source.Scanner) ?? 0));
        if (signatureFlags != 0)
        {
            result[NameToken.SigFlags] = new NumericToken(signatureFlags);
        }

        if (forms.Any(form => form.Source.Resolve<BooleanToken>(form.Dictionary.TryGet(NameToken.NeedAppearances, out var n) ? n : null) is { Data: true }))
        {
            result[NameToken.NeedAppearances] = BooleanToken.True;
        }

        catalog[NameToken.AcroForm] = context.Writer.WriteToken(new DictionaryToken(result));
    }

    /// <summary>
    /// Walks a field and its kids, leaving out what no page kept holds and filtering each kept field's kids.
    /// </summary>
    private static Field? Visit(AssemblyContext context, Form form, IToken token, int depth, HashSet<IndirectReference> visited)
    {
        var source = form.Source;
        var reference = token as IndirectReferenceToken;
        if (reference is not null && !visited.Add(reference.Data) || source.Resolve<DictionaryToken>(token) is not { } dictionary)
        {
            return null;
        }

        if (depth > DeepestField)
        {
            return new Field(token, dictionary, kept: true, hasWidgets: false);
        }

        var isWidget = AssemblyTokens.IsName(dictionary, NameToken.Subtype, NameToken.Widget, source.Scanner);
        var kids = source.Array(dictionary, NameToken.Kids)?.Data ?? [];
        var children = kids.Select(kid => Visit(context, form, kid, depth + 1, visited)).Where(child => child is not null).Select(child => child!).ToList();
        var hasWidgets = isWidget || children.Any(child => child.HasWidgets);
        var kept = isWidget
            ? reference is null || context.KeepsAnnotation(source, reference.Data)
            : children.Any(child => child.Kept) || !hasWidgets;

        if (!kept)
        {
            if (reference is not null)
            {
                context.Rewrites.Drop(source, reference.Data);
            }

            return new Field(token, dictionary, kept: false, hasWidgets);
        }

        var edits = new List<Func<DictionaryToken, DictionaryToken>>();

        var keptKids = children.Where(child => child.Kept).Select(child => child.Token).ToList();
        if (keptKids.Count != kids.Count || children.Any(child => child.Kept && child.Token is DictionaryToken))
        {
            edits.Add(d => d.With(NameToken.Kids, new ArrayToken(keptKids)));
        }

        if (form.ResourceRenames.Count > 0
            && AssemblyTokens.Text(dictionary.TryGet(NameToken.Da, out var da) ? da : null, source.Scanner) is { } appearance)
        {
            var renamed = form.Renamed(appearance);
            if (renamed != appearance)
            {
                edits.Add(d => d.With(NameToken.Da, new StringToken(renamed)));
            }
        }

        return new Field(context.Rewrites.Apply(source, token, edits), dictionary, kept: true, hasWidgets);
    }

    /// <summary>
    /// Names each default resource of a form after the first apart from the names already taken.
    /// </summary>
    private static void RenameResources(Form form, Dictionary<string, HashSet<string>> taken)
    {
        if (form.Source.Dictionary(form.Dictionary, NameToken.Dr) is not { } resources)
        {
            return;
        }

        foreach (var category in resources.Data)
        {
            if (!taken.TryGetValue(category.Key, out var names))
            {
                names = new HashSet<string>(StringComparer.Ordinal);
                taken[category.Key] = names;
            }

            if (form.Source.Resolve<DictionaryToken>(category.Value) is not { } entries)
            {
                continue;
            }

            var renames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries.Data)
            {
                renames[entry.Key] = names.Add(entry.Key) ? entry.Key : Unique(entry.Key, names);
                if (renames[entry.Key] != entry.Key && category.Key == NameToken.Font.Data)
                {
                    form.ResourceRenames[entry.Key] = renames[entry.Key];
                }
            }

            form.Resources[category.Key] = renames;
        }
    }

    private Dictionary<NameToken, IToken> Resources(AssemblyContext context)
    {
        var merged = new Dictionary<string, Dictionary<NameToken, IToken>>(StringComparer.Ordinal);
        foreach (var form in forms)
        {
            if (form.Source.Dictionary(form.Dictionary, NameToken.Dr) is not { } resources)
            {
                continue;
            }

            foreach (var category in resources.Data)
            {
                if (form.Source.Resolve<DictionaryToken>(category.Value) is not { } entries)
                {
                    continue;
                }

                if (!merged.TryGetValue(category.Key, out var target))
                {
                    target = new Dictionary<NameToken, IToken>();
                    merged[category.Key] = target;
                }

                var renames = form.Resources[category.Key];
                foreach (var entry in entries.Data)
                {
                    target[NameToken.Create(renames[entry.Key])] = context.Copy(form.Source, entry.Value);
                }
            }
        }

        return merged.ToDictionary(category => NameToken.Create(category.Key), category => (IToken)new DictionaryToken(category.Value));
    }

    private static string Unique(string name, HashSet<string> taken)
    {
        for (var i = 2; ; i++)
        {
            var candidate = name + "_" + i;
            if (taken.Add(candidate))
            {
                return candidate;
            }
        }
    }

    private sealed class Form
    {
        public Form(AssemblySource source, DictionaryToken dictionary)
        {
            Source = source;
            Dictionary = dictionary;
        }

        public AssemblySource Source { get; }

        public DictionaryToken Dictionary { get; }

        /// <summary>
        /// The kept root fields, as source tokens with their edits.
        /// </summary>
        public List<IToken> Roots { get; } = new();

        /// <summary>
        /// Each default resource's name in the output, by category and source name.
        /// </summary>
        public Dictionary<string, Dictionary<string, string>> Resources { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The default fonts renamed, which a default appearance names.
        /// </summary>
        public Dictionary<string, string> ResourceRenames { get; } = new(StringComparer.Ordinal);

        public string Renamed(string appearance)
            => ResourceRenames.Count == 0
                ? appearance
                : ResourceName.Replace(appearance, match => ResourceRenames.TryGetValue(match.Groups[1].Value, out var renamed) ? "/" + renamed : match.Value);
    }

    private sealed class Field
    {
        public Field(IToken token, DictionaryToken dictionary, bool kept, bool hasWidgets)
        {
            Token = token;
            Dictionary = dictionary;
            Kept = kept;
            HasWidgets = hasWidgets;
        }

        /// <summary>
        /// The field as its parent lists it: its reference, or a direct field with its edits made.
        /// </summary>
        public IToken Token { get; }

        public DictionaryToken Dictionary { get; }

        public bool Kept { get; }

        public bool HasWidgets { get; }
    }
}
