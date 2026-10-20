namespace UglyToad.PdfPig.Writer;

using System;

/// <summary>
/// A page of a document being assembled: one page of an open source document.
/// </summary>
public readonly struct AssembledPage
{
    /// <summary>
    /// The document the page is taken from. It must stay open until the assembly is written.
    /// </summary>
    public PdfDocument Document { get; }

    /// <summary>
    /// The page's number in <see cref="Document"/>, from 1.
    /// </summary>
    public int PageNumber { get; }

    /// <summary>
    /// How the page's content is tagged when the assembled document is a tagged PDF and <see cref="Document"/> is not:
    /// its whole content becomes one structure element of this role. Null leaves it untagged.
    /// </summary>
    public AssembledContentTag? Tag { get; }

    /// <summary>
    /// Takes page <paramref name="pageNumber"/> of <paramref name="document"/>.
    /// </summary>
    public AssembledPage(PdfDocument document, int pageNumber, AssembledContentTag? tag = null)
    {
        if (pageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), pageNumber, "Pages are numbered from 1.");
        }

        Document = document ?? throw new ArgumentNullException(nameof(document));
        PageNumber = pageNumber;
        Tag = tag;
    }
}
