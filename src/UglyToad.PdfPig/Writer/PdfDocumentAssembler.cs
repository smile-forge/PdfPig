namespace UglyToad.PdfPig.Writer;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Assembling;

/// <summary>
/// Writes a document made of pages taken from open documents, in any order, together with everything those documents
/// hold beyond their pages: the outline, the interactive form, page labels, named destinations and the other name
/// trees, the logical structure of a tagged document, article threads and optional content, each re-pointed to where
/// its pages now are. What lived only on a page that is left out goes with that page; nothing else is dropped.
/// </summary>
public static class PdfDocumentAssembler
{
    /// <summary>
    /// Writes <paramref name="pages"/>, in order, as a new document to <paramref name="output"/>.
    /// </summary>
    /// <param name="pages">The pages of the new document. A page of a source document may appear only once.</param>
    /// <param name="output">A writable stream, left open. An assembly that is cancelled or fails leaves it incomplete.</param>
    /// <param name="options">The document the catalog and the document information come from, and the version written.</param>
    /// <param name="cancellationToken">Checked between pages and between the parts of the documents carried.</param>
    /// <exception cref="AssemblyRefusedException">A source document cannot be assembled from, such as one with an XFA form.</exception>
    public static void Assemble(
        IReadOnlyList<AssembledPage> pages,
        Stream output,
        AssemblyOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (pages is null)
        {
            throw new ArgumentNullException(nameof(pages));
        }

        if (output is null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        if (pages.Count == 0)
        {
            throw new ArgumentException("A document needs at least one page.", nameof(pages));
        }

        new DocumentAssembly(pages, output, options ?? new AssemblyOptions(), cancellationToken).Write();
    }
}
