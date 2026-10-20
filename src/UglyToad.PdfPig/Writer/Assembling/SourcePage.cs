namespace UglyToad.PdfPig.Writer.Assembling;

using System.Collections.Generic;
using Core;
using Tokens;

/// <summary>
/// A page of a source document, and where it goes in the assembly.
/// </summary>
internal sealed class SourcePage
{
    public SourcePage(
        int number,
        IndirectReference reference,
        DictionaryToken dictionary,
        IReadOnlyList<DictionaryToken> ancestors,
        IReadOnlyList<IToken> annotations)
    {
        Number = number;
        Reference = reference;
        Dictionary = dictionary;
        Ancestors = ancestors;
        Annotations = annotations;
    }

    public int Number { get; }

    public IndirectReference Reference { get; }

    public DictionaryToken Dictionary { get; }

    /// <summary>
    /// The page tree nodes above the page, root first: where an inherited attribute it lacks comes from.
    /// </summary>
    public IReadOnlyList<DictionaryToken> Ancestors { get; }

    /// <summary>
    /// The page's annotations, as its <c>/Annots</c> array holds them.
    /// </summary>
    public IReadOnlyList<IToken> Annotations { get; }

    /// <summary>
    /// Where the page goes in the assembly, or null when it is left out.
    /// </summary>
    public AssemblySlot? Slot { get; set; }

    public bool IsKept => Slot is not null;
}
