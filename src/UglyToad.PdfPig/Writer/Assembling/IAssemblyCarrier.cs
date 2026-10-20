namespace UglyToad.PdfPig.Writer.Assembling;

using System.Collections.Generic;
using Tokens;

/// <summary>
/// One part of the documents an assembly carries beyond their pages: the outline, the form, the structure tree…
/// </summary>
internal interface IAssemblyCarrier
{
    /// <summary>
    /// Before any page is copied: claims, edits and leaves out the source objects this part changes.
    /// </summary>
    void Prepare(AssemblyContext context);

    /// <summary>
    /// After the pages are written: writes what this part adds and puts its entries in the catalog.
    /// </summary>
    void Complete(AssemblyContext context, IDictionary<NameToken, IToken> catalog);
}
