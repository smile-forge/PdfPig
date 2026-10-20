namespace UglyToad.PdfPig.Writer;

using System;

/// <summary>
/// The structure element an untagged page's content becomes in a tagged assembly — a <c>Figure</c> with its
/// alternate description, for a page that is a single picture.
/// </summary>
public sealed class AssembledContentTag
{
    /// <summary>
    /// The structure type of the element, such as <c>Figure</c>.
    /// </summary>
    public string Role { get; }

    /// <summary>
    /// The element's alternate description (<c>/Alt</c>), read in place of the content; null for none.
    /// </summary>
    public string? AlternateDescription { get; }

    /// <summary>
    /// Tags the page's content as one element of <paramref name="role"/>.
    /// </summary>
    public AssembledContentTag(string role, string? alternateDescription = null)
    {
        if (string.IsNullOrWhiteSpace(role))
        {
            throw new ArgumentException("A structure element needs a role.", nameof(role));
        }

        Role = role;
        AlternateDescription = alternateDescription;
    }
}
