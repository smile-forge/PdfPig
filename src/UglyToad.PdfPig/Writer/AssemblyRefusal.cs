namespace UglyToad.PdfPig.Writer;

/// <summary>
/// Why a document cannot be assembled from.
/// </summary>
public enum AssemblyRefusal
{
    /// <summary>
    /// The document carries an XFA form, which lays out the whole document itself and cannot follow its pages into
    /// another order or another document.
    /// </summary>
    XfaForm = 0,

    /// <summary>
    /// A page placed on a sheet has content that cannot be read, which drawing it there needs.
    /// </summary>
    UnreadableContent = 1,
}
