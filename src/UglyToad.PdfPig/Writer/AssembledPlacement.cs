namespace UglyToad.PdfPig.Writer;

using System;
using Core;

/// <summary>
/// A page placed on a sheet of an assembled document: the page — or the part of it — as it is shown, put where
/// <see cref="Place"/> takes it, at the size it takes it to.
/// </summary>
public readonly struct AssembledPlacement
{
    private const double Tolerance = 1e-9;

    /// <summary>
    /// The page placed: its document, number, tag, and the region of it shown.
    /// </summary>
    public AssembledPage Page { get; }

    /// <summary>
    /// From the page as it is shown — its region, or its crop box, turned by its rotation, with the origin at its bottom
    /// left and measured in points — to the sheet's user space. It may move the page, scale it along either axis and
    /// turn it a quarter turn at a time; it may not mirror or skew it, so what lies on the page in rectangles stays in
    /// rectangles on the sheet.
    /// </summary>
    public TransformationMatrix Place { get; }

    /// <summary>
    /// Places <paramref name="page"/> on a sheet by <paramref name="place"/>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="place"/> mirrors, skews or flattens the page.</exception>
    public AssembledPlacement(AssembledPage page, TransformationMatrix place)
    {
        if (page.Document is null)
        {
            throw new ArgumentException("A placement places a page.", nameof(page));
        }

        if (!IsAxisPreserving(place))
        {
            throw new ArgumentException(
                "A page is placed by moving it, scaling it along either axis and turning it a quarter turn at a time; this transform mirrors, skews or flattens it.",
                nameof(place));
        }

        Page = page;
        Place = place;
    }

    /// <summary>
    /// Whether <paramref name="matrix"/> is a quarter turn of positive scales and a move: either the axes stay put, each
    /// scaled the same way round, or they swap as a quarter turn swaps them.
    /// </summary>
    internal static bool IsAxisPreserving(TransformationMatrix matrix)
    {
        if (Math.Abs(matrix[0, 2]) > Tolerance || Math.Abs(matrix[1, 2]) > Tolerance || Math.Abs(matrix[2, 2] - 1) > Tolerance)
        {
            return false;
        }

        var scale = Math.Max(Math.Max(Math.Abs(matrix.A), Math.Abs(matrix.B)), Math.Max(Math.Abs(matrix.C), Math.Abs(matrix.D)));
        if (scale <= 0)
        {
            return false;
        }

        var zero = scale * Tolerance;
        if (Math.Abs(matrix.B) <= zero && Math.Abs(matrix.C) <= zero)
        {
            return Math.Abs(matrix.A) > zero && Math.Abs(matrix.D) > zero && Math.Sign(matrix.A) == Math.Sign(matrix.D);
        }

        if (Math.Abs(matrix.A) <= zero && Math.Abs(matrix.D) <= zero)
        {
            return Math.Abs(matrix.B) > zero && Math.Abs(matrix.C) > zero && Math.Sign(matrix.B) == -Math.Sign(matrix.C);
        }

        return false;
    }
}
