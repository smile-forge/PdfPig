namespace UglyToad.PdfPig.Writer;

/// <summary>
/// What an assembly is built on, beyond its pages.
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
}
