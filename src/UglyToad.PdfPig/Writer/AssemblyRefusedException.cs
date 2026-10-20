namespace UglyToad.PdfPig.Writer;

using System;

/// <summary>
/// A source document cannot be assembled from: what it holds would not survive having its pages moved.
/// </summary>
public sealed class AssemblyRefusedException : Exception
{
    /// <summary>
    /// The document refused.
    /// </summary>
    public PdfDocument Document { get; }

    /// <summary>
    /// Why it was refused.
    /// </summary>
    public AssemblyRefusal Reason { get; }

    /// <summary>
    /// Refuses <paramref name="document"/> for <paramref name="reason"/>.
    /// </summary>
    public AssemblyRefusedException(PdfDocument document, AssemblyRefusal reason, string message)
        : base(message)
    {
        Document = document;
        Reason = reason;
    }
}
