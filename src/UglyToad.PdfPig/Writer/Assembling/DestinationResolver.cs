namespace UglyToad.PdfPig.Writer.Assembling;

using System.Collections.Generic;
using Core;
using Parser.Parts;
using Tokens;

/// <summary>
/// Where a destination in a source goes: an explicit destination's page, or a named one looked up in the source's
/// <c>/Dests</c> and its name tree of destinations.
/// </summary>
internal sealed class DestinationResolver
{
    private const int MostIndirections = 8;

    private readonly Dictionary<AssemblySource, Dictionary<string, IToken>> named = new();

    /// <summary>
    /// Resolves a destination: an array, a name or string naming one, or a dictionary holding one in <c>/D</c>.
    /// </summary>
    public ResolvedDestination Resolve(AssemblySource source, IToken? destination)
        => Resolve(source, destination, null, 0);

    /// <summary>
    /// Resolves the destination of a go-to action; anything else goes nowhere in the document.
    /// </summary>
    public ResolvedDestination ResolveAction(AssemblySource source, IToken? action)
    {
        var dictionary = source.Resolve<DictionaryToken>(action);
        if (dictionary is null
            || !AssemblyTokens.IsName(dictionary, NameToken.S, NameToken.GoTo, source.Scanner)
            || !dictionary.TryGet(NameToken.D, out var destination))
        {
            return default;
        }

        return Resolve(source, destination);
    }

    /// <summary>
    /// The destinations the source names, by name.
    /// </summary>
    public IReadOnlyDictionary<string, IToken> Named(AssemblySource source)
    {
        if (named.TryGetValue(source, out var names))
        {
            return names;
        }

        names = new Dictionary<string, IToken>();

        var tree = source.Dictionary(source.Catalog, NameToken.Names) is { } nameTrees
            ? source.Dictionary(nameTrees, NameToken.Dests)
            : null;
        if (tree is not null)
        {
            foreach (var entry in NameTreeEntries.Read(tree, source.Scanner))
            {
                if (!names.ContainsKey(entry.Key.Data))
                {
                    names[entry.Key.Data] = entry.Value;
                }
            }
        }

        if (source.Dictionary(source.Catalog, NameToken.Dests) is { } dests)
        {
            foreach (var entry in dests.Data)
            {
                if (!names.ContainsKey(entry.Key))
                {
                    names[entry.Key] = entry.Value;
                }
            }
        }

        named[source] = names;
        return names;
    }

    private ResolvedDestination Resolve(AssemblySource source, IToken? destination, string? name, int depth)
    {
        if (destination is null || depth > MostIndirections)
        {
            return default;
        }

        if (destination is IndirectReferenceToken reference)
        {
            destination = DirectObjectFinder.Get<IToken>(reference.Data, source.Scanner);
        }

        switch (destination)
        {
            case ArrayToken array when array.Length > 0:
                return new ResolvedDestination(array.Data[0] is IndirectReferenceToken page ? page.Data : null, array, name);
            case NameToken destinationName:
                return ResolveNamed(source, destinationName.Data, depth);
            case IDataToken<string> destinationText:
                return ResolveNamed(source, destinationText.Data, depth);
            case DictionaryToken dictionary when dictionary.TryGet(NameToken.D, out var inner):
                return Resolve(source, inner, name, depth + 1);
            default:
                return default;
        }
    }

    private ResolvedDestination ResolveNamed(AssemblySource source, string name, int depth)
        => Named(source).TryGetValue(name, out var destination)
            ? Resolve(source, destination, name, depth + 1)
            : new ResolvedDestination(null, null, name);
}

/// <summary>
/// A resolved destination: the source page it goes to, the explicit destination that says so, and the name it was
/// reached by, if any.
/// </summary>
internal readonly struct ResolvedDestination
{
    public ResolvedDestination(IndirectReference? page, ArrayToken? explicitDestination, string? name)
    {
        Page = page;
        Explicit = explicitDestination;
        Name = name;
    }

    public IndirectReference? Page { get; }

    public ArrayToken? Explicit { get; }

    public string? Name { get; }

    public bool IsNamed => Name is not null;
}
