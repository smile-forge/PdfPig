namespace UglyToad.PdfPig.Writer;

/// <summary>
/// What an assembly is built on, beyond its pages, and the images it draws anew.
/// </summary>
public sealed class AssemblyOptions
{
    /// <summary>
    /// The document the assembly is built on: its outline, form, page labels, names and viewer settings come first,
    /// and its document information is kept. Null takes the document of the first page. It need not give a page.
    /// </summary>
    public PdfDocument? Primary { get; set; }

    /// <summary>
    /// The document information to write. Null keeps the primary document's, with its modification date set to now.
    /// </summary>
    public PdfDocumentBuilder.DocumentInformationBuilder? Information { get; set; }

    /// <summary>
    /// The lowest version to write. The newest version among the source documents is written when it is higher.
    /// </summary>
    public double? Version { get; set; }

    /// <summary>
    /// Pictures written in place of images the source documents hold: every page that draws one draws its picture instead.
    /// An image is replaced once; a picture for a document no page comes from is not written.
    /// </summary>
    public IReadOnlyList<AssembledImageReplacement> ImageReplacements { get; set; } = Array.Empty<AssembledImageReplacement>();
}
