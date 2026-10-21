namespace UglyToad.PdfPig.Writer.Assembling;

using System.Collections.Generic;
using System.Linq;
using Tokens;

/// <summary>
/// The rest of the catalog. Optional content and output intents from every source are joined; the opening action is
/// the primary's, unless it goes to a page left out; associated files are joined; and every other entry — viewer
/// preferences, page mode and layout, language, metadata, document actions, the portfolio, extensions, permissions,
/// legal attestations, the security store — is carried from the primary, or from the first source that has it.
/// </summary>
internal sealed class CatalogCarrier : IAssemblyCarrier
{
    /// <summary>
    /// The entries a carrier writes, or that are written from the pages: never carried as they stand.
    /// </summary>
    private static readonly HashSet<string> Owned =
    [
        NameToken.Type.Data, NameToken.Pages.Data, NameToken.Outlines.Data, NameToken.AcroForm.Data, NameToken.PageLabels.Data,
        NameToken.Names.Data, NameToken.Dests.Data, NameToken.StructTreeRoot.Data, NameToken.MarkInfo.Data, NameToken.Threads.Data,
        NameToken.Ocproperties.Data, NameToken.OutputIntents.Data, NameToken.OpenAction.Data, NameToken.Version.Data,
        AssemblyNames.AssociatedFiles.Data,
    ];

    public void Prepare(AssemblyContext context)
    {
    }

    public void Complete(AssemblyContext context, IDictionary<NameToken, IToken> catalog)
    {
        OptionalContent(context, catalog);
        Joined(context, catalog, NameToken.OutputIntents);
        Joined(context, catalog, AssemblyNames.AssociatedFiles);
        OpenAction(context, catalog);

        foreach (var source in context.Sources)
        {
            foreach (var entry in source.Catalog.Data)
            {
                var name = NameToken.Create(entry.Key);
                if (Owned.Contains(entry.Key) || catalog.ContainsKey(name))
                {
                    continue;
                }

                var copied = context.Copy(source, entry.Value);
                if (!context.IsNull(copied))
                {
                    catalog[name] = copied;
                }
            }
        }
    }

    private static void Joined(AssemblyContext context, IDictionary<NameToken, IToken> catalog, NameToken key)
    {
        var items = context.Sources
            .SelectMany(source => (source.Array(source.Catalog, key)?.Data ?? []).Select(item => context.Copy(source, item)))
            .Where(item => !context.IsNull(item))
            .Distinct()
            .ToList();
        if (items.Count > 0)
        {
            catalog[key] = new ArrayToken(items);
        }
    }

    private static void OpenAction(AssemblyContext context, IDictionary<NameToken, IToken> catalog)
    {
        var primary = context.Primary;
        if (!primary.Catalog.TryGet(NameToken.OpenAction, out var action))
        {
            return;
        }

        var resolved = primary.Resolve<IToken>(action);
        var target = resolved is ArrayToken
            ? context.Destinations.Resolve(primary, resolved)
            : context.Destinations.ResolveAction(primary, resolved);
        if (context.IsPageLeftOut(primary, target.Page))
        {
            return;
        }

        if (resolved is ArrayToken)
        {
            catalog[NameToken.OpenAction] = context.Copy(primary, context.Retargeted(primary, resolved, target));
        }
        else if (resolved is DictionaryToken goTo && goTo.TryGet(NameToken.D, out var destination))
        {
            var retargeted = context.Retargeted(primary, destination, target);
            catalog[NameToken.OpenAction] = context.Copy(primary, ReferenceEquals(retargeted, destination) ? action : goTo.With(NameToken.D, retargeted));
        }
        else
        {
            catalog[NameToken.OpenAction] = context.Copy(primary, action);
        }
    }

    /// <summary>
    /// One set of optional content: every group of every source, the primary's default configuration with the
    /// others' groups, order and states added to it, and every alternate configuration.
    /// </summary>
    private static void OptionalContent(AssemblyContext context, IDictionary<NameToken, IToken> catalog)
    {
        var parts = context.Sources
            .Select(source => (Source: source, Properties: source.Dictionary(source.Catalog, NameToken.Ocproperties)))
            .Where(part => part.Properties is not null)
            .ToList();
        if (parts.Count == 0)
        {
            return;
        }

        var (baseSource, baseProperties) = parts[0];
        var result = AssemblyTokens.Mutable((DictionaryToken)context.Copy(baseSource, baseProperties!));

        result[NameToken.Ocgs] = new ArrayToken(parts
            .SelectMany(part => (part.Source.Array(part.Properties!, NameToken.Ocgs)?.Data ?? []).Select(group => context.Copy(part.Source, group)))
            .Where(group => !context.IsNull(group))
            .Distinct()
            .ToList());

        var configuration = baseSource.Dictionary(baseProperties!, NameToken.D) is { } baseDefault
            ? AssemblyTokens.Mutable((DictionaryToken)context.Copy(baseSource, baseDefault))
            : new Dictionary<NameToken, IToken>();
        foreach (var (source, properties) in parts.Skip(1))
        {
            if (source.Dictionary(properties!, NameToken.D) is not { } other)
            {
                continue;
            }

            foreach (var key in new[] { NameToken.On, NameToken.Off, AssemblyNames.Locked, AssemblyNames.RadioButtonGroups, AssemblyNames.AppearanceState, NameToken.Order })
            {
                var added = (source.Array(other, key)?.Data ?? []).Select(item => context.Copy(source, item)).Where(item => !context.IsNull(item)).ToList();
                if (added.Count == 0)
                {
                    continue;
                }

                var existing = configuration.TryGetValue(key, out var current) && current is ArrayToken array ? array.Data : [];
                configuration[key] = new ArrayToken(existing.Concat(added).ToList());
            }
        }

        result[NameToken.D] = new DictionaryToken(configuration);

        var configurations = parts
            .SelectMany(part => (part.Source.Array(part.Properties!, AssemblyNames.Configs)?.Data ?? []).Select(item => context.Copy(part.Source, item)))
            .Where(item => !context.IsNull(item))
            .ToList();
        if (configurations.Count > 0)
        {
            result[AssemblyNames.Configs] = new ArrayToken(configurations);
        }

        catalog[NameToken.Ocproperties] = context.Writer.WriteToken(new DictionaryToken(result));
    }
}
