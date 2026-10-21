namespace UglyToad.PdfPig.Writer;

using System;
using Core;

/// <summary>
/// A page of a document being assembled: one page of an open source document, or one part of it.
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
    /// The part of the page shown, in the page's default user space — one page of several printed to a sheet. It is
    /// written as the page's crop box, its other boxes held to it; the annotations, article beads and tagged content
    /// on the page go to the part they lie in. The same page may be assembled once for each of its parts. Null shows
    /// the page as it is.
    /// </summary>
    public PdfRectangle? Region { get; }

    /// <summary>
    /// Takes page <paramref name="pageNumber"/> of <paramref name="document"/>, or the <paramref name="region"/> of it.
    /// </summary>
    public AssembledPage(PdfDocument document, int pageNumber, AssembledContentTag? tag = null, PdfRectangle? region = null)
    {
        if (pageNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageNumber), pageNumber, "Pages are numbered from 1.");
        }

        if (region is { } part && (part.Width <= 0 || part.Height <= 0))
        {
            throw new ArgumentException("A part of a page has an area.", nameof(region));
        }

        Document = document ?? throw new ArgumentNullException(nameof(document));
        PageNumber = pageNumber;
        Tag = tag;
        Region = region;
    }
}
