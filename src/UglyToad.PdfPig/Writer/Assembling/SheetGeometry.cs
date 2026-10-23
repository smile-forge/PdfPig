namespace UglyToad.PdfPig.Writer.Assembling;

using System;
using System.Collections.Generic;
using System.Linq;
using Core;
using Tokens;

/// <summary>
/// What lies on a page placed on a sheet, moved to where the page is placed: rectangles, points, insets, turns and
/// destinations, through a transform that keeps axes as axes — a move, a scale along either axis, a quarter turn.
/// </summary>
internal static class SheetGeometry
{
    /// <summary>
    /// The rectangle <paramref name="rectangle"/> becomes: the bounds of its corners moved, which is the rectangle
    /// itself, moved, since the transform keeps axes as axes.
    /// </summary>
    public static PdfRectangle Bounds(TransformationMatrix transform, PdfRectangle rectangle)
    {
        var corners = new[]
        {
            transform.Transform(new PdfPoint(rectangle.Left, rectangle.Bottom)),
            transform.Transform(new PdfPoint(rectangle.Right, rectangle.Bottom)),
            transform.Transform(new PdfPoint(rectangle.Left, rectangle.Top)),
            transform.Transform(new PdfPoint(rectangle.Right, rectangle.Top)),
        };

        return new PdfRectangle(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
    }

    /// <summary>
    /// Whether the transform turns the page a quarter turn either way, so what runs across it runs up the sheet.
    /// </summary>
    public static bool SwapsAxes(TransformationMatrix transform) => Math.Abs(transform.A) < Math.Abs(transform.B);

    /// <summary>
    /// How far the transform turns the page, counterclockwise, in degrees: 0, 90, 180 or 270.
    /// </summary>
    public static int Turn(TransformationMatrix transform)
    {
        var degrees = Math.Atan2(transform.B, transform.A) * 180 / Math.PI;
        return ((int)Math.Round(degrees / 90) * 90 % 360 + 360) % 360;
    }

    /// <summary>
    /// The turn alone, about the origin: what an appearance drawn upright must be turned by to lie as its page does.
    /// </summary>
    public static TransformationMatrix Turning(TransformationMatrix transform)
        => Turn(transform) switch
        {
            90  => TransformationMatrix.FromValues(0, 1, -1, 0, 0, 0),
            180 => TransformationMatrix.FromValues(-1, 0, 0, -1, 0, 0),
            270 => TransformationMatrix.FromValues(0, -1, 1, 0, 0, 0),
            _   => TransformationMatrix.Identity,
        };

    /// <summary>
    /// How much the transform scales a length, taken as the mean of its two scales.
    /// </summary>
    public static double Scale(TransformationMatrix transform)
        => Math.Sqrt(Math.Abs(transform.A * transform.D - transform.B * transform.C));

    public static ArrayToken Rectangle(PdfRectangle rectangle) => new([
        new NumericToken(rectangle.Left), new NumericToken(rectangle.Bottom),
        new NumericToken(rectangle.Right), new NumericToken(rectangle.Top),
    ]);

    public static ArrayToken Matrix(TransformationMatrix transform) => new([
        new NumericToken(transform.A), new NumericToken(transform.B),
        new NumericToken(transform.C), new NumericToken(transform.D),
        new NumericToken(transform.E), new NumericToken(transform.F),
    ]);

    /// <summary>
    /// A rectangle written as four numbers, normalised; null when it is written otherwise.
    /// </summary>
    public static PdfRectangle? Rectangle(AssemblySource source, IToken? token)
    {
        if (Numbers(source, token) is not { Count: 4 } n)
        {
            return null;
        }

        return new PdfRectangle(Math.Min(n[0], n[2]), Math.Min(n[1], n[3]), Math.Max(n[0], n[2]), Math.Max(n[1], n[3]));
    }

    /// <summary>
    /// A matrix written as six numbers; null when it is written otherwise.
    /// </summary>
    public static TransformationMatrix? Matrix(AssemblySource source, IToken? token)
        => Numbers(source, token) is { Count: 6 } n ? TransformationMatrix.FromValues(n[0], n[1], n[2], n[3], n[4], n[5]) : null;

    /// <summary>
    /// An array of numbers read as pairs of coordinates, each pair moved; null when it is not an array of numbers in pairs.
    /// </summary>
    public static ArrayToken? Points(AssemblySource source, IToken? token, TransformationMatrix transform)
    {
        if (Numbers(source, token) is not { } n || n.Count % 2 != 0)
        {
            return null;
        }

        var moved = new List<IToken>(n.Count);
        for (var i = 0; i < n.Count; i += 2)
        {
            var point = transform.Transform(new PdfPoint(n[i], n[i + 1]));
            moved.Add(new NumericToken(point.X));
            moved.Add(new NumericToken(point.Y));
        }

        return new ArrayToken(moved);
    }

    /// <summary>
    /// The insets of a rectangle — left, top, right, bottom, as an annotation's <c>/RD</c> holds them — once the
    /// rectangle is moved: the inner rectangle moved within the outer one, so a quarter turn passes each inset to the
    /// side it now lies on and a scale scales it.
    /// </summary>
    public static ArrayToken? Insets(AssemblySource source, IToken? token, PdfRectangle outer, TransformationMatrix transform)
    {
        if (Numbers(source, token) is not { Count: 4 } d)
        {
            return null;
        }

        var inner = new PdfRectangle(outer.Left + d[0], outer.Bottom + d[3], outer.Right - d[2], outer.Top - d[1]);
        var movedOuter = Bounds(transform, outer);
        var movedInner = Bounds(transform, inner);
        return new ArrayToken([
            new NumericToken(movedInner.Left - movedOuter.Left), new NumericToken(movedOuter.Top - movedInner.Top),
            new NumericToken(movedOuter.Right - movedInner.Right), new NumericToken(movedInner.Bottom - movedOuter.Bottom),
        ]);
    }

    /// <summary>
    /// An explicit destination into a page placed on a sheet, moved with it: the page made <paramref name="sheet"/>, and
    /// its coordinates moved. A destination is the part of the page it brings into view — from its point, or its
    /// line, to the far corner of what is shown — so that part is moved, and the destination made the one that brings
    /// the moved part into view: across a quarter turn a line along the page becomes one up the sheet, and a
    /// destination that fits the page fits the rectangle it now covers.
    /// </summary>
    public static ArrayToken Destination(
        AssemblySource source, ArrayToken destination, IToken sheet, PdfRectangle shown, TransformationMatrix transform)
    {
        double? At(int index) => index < destination.Length ? source.Resolve<NumericToken>(destination.Data[index])?.Double : null;

        PdfRectangle Area(double left, double bottom, double right, double top)
            => Bounds(transform, new PdfRectangle(Math.Min(left, right), Math.Min(bottom, top), Math.Max(left, right), Math.Max(bottom, top)));

        IToken Number(double value) => new NumericToken(value);

        var kind = destination.Length > 1 ? AssemblyTokens.Name(destination.Data[1], source.Scanner) : null;
        var swaps = SwapsAxes(transform);
        switch (kind?.Data)
        {
            case "XYZ":
            {
                var area = Area(At(2) ?? shown.Left, shown.Bottom, shown.Right, At(3) ?? shown.Top);
                var zoom = destination.Length > 4 ? destination.Data[4] : NullToken.Instance;
                return new ArrayToken([sheet, kind, Number(area.Left), Number(area.Top), zoom]);
            }

            case "FitH":
            case "FitBH":
            {
                var area = Area(shown.Left, shown.Bottom, shown.Right, At(2) ?? shown.Top);
                return swaps
                    ? new ArrayToken([sheet, NameToken.Create(kind.Data == "FitH" ? "FitV" : "FitBV"), Number(area.Left)])
                    : new ArrayToken([sheet, kind, Number(area.Top)]);
            }

            case "FitV":
            case "FitBV":
            {
                var area = Area(At(2) ?? shown.Left, shown.Bottom, shown.Right, shown.Top);
                return swaps
                    ? new ArrayToken([sheet, NameToken.Create(kind.Data == "FitV" ? "FitH" : "FitBH"), Number(area.Top)])
                    : new ArrayToken([sheet, kind, Number(area.Left)]);
            }

            case "FitR":
            {
                var area = Area(At(2) ?? shown.Left, At(3) ?? shown.Bottom, At(4) ?? shown.Right, At(5) ?? shown.Top);
                return new ArrayToken([sheet, kind, Number(area.Left), Number(area.Bottom), Number(area.Right), Number(area.Top)]);
            }

            default:
            {
                // Fit and FitB fit the page, which on a sheet is the rectangle it now covers.
                var area = Bounds(transform, shown);
                return new ArrayToken([sheet, NameToken.Create("FitR"), Number(area.Left), Number(area.Bottom), Number(area.Right), Number(area.Top)]);
            }
        }
    }

    private static IReadOnlyList<double>? Numbers(AssemblySource source, IToken? token)
    {
        if (source.Resolve<ArrayToken>(token) is not { } array)
        {
            return null;
        }

        var numbers = new List<double>(array.Length);
        foreach (var item in array.Data)
        {
            if (source.Resolve<NumericToken>(item) is not { } number)
            {
                return null;
            }

            numbers.Add(number.Double);
        }

        return numbers;
    }
}
