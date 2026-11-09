namespace UglyToad.PdfPig.Writer;

using System;
using System.IO;
using Core;
using Images;
using Tokens;

/// <summary>
/// A picture written in place of an image a source document holds: every page that draws the image — wherever it is
/// assembled, on whatever sheet it is placed — draws the picture instead, in the place the image had, however large the
/// picture is. A JPEG is written as it is; a PNG's pixels are compressed, its alpha channel a soft mask.
/// </summary>
public sealed class AssembledImageReplacement
{
    private readonly byte[] picture;
    private readonly bool isJpeg;

    private AssembledImageReplacement(PdfDocument document, IndirectReference image, byte[] picture, bool isJpeg)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Image = image;
        this.picture = picture ?? throw new ArgumentNullException(nameof(picture));
        this.isJpeg = isJpeg;
    }

    /// <summary>
    /// The document whose image is replaced.
    /// </summary>
    public PdfDocument Document { get; }

    /// <summary>
    /// The image replaced: the indirect object of its image XObject.
    /// </summary>
    public IndirectReference Image { get; }

    /// <summary>
    /// <paramref name="jpeg"/>, a JPEG file, drawn in place of <paramref name="image"/> of <paramref name="document"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The bytes are not a JPEG file.</exception>
    public static AssembledImageReplacement Jpeg(PdfDocument document, IndirectReference image, byte[] jpeg)
    {
        if (jpeg is null)
        {
            throw new ArgumentNullException(nameof(jpeg));
        }

        using (var stream = new MemoryStream(jpeg))
        {
            JpegHandler.GetInformation(stream);
        }

        return new AssembledImageReplacement(document, image, jpeg, isJpeg: true);
    }

    /// <summary>
    /// <paramref name="png"/>, a PNG file, drawn in place of <paramref name="image"/> of <paramref name="document"/>.
    /// </summary>
    public static AssembledImageReplacement Png(PdfDocument document, IndirectReference image, byte[] png)
        => new(document, image, png, isJpeg: false);

    /// <summary>
    /// Writes the picture as an image XObject — its soft mask first, where it has one — and gives its reference.
    /// </summary>
    internal IndirectReferenceToken Write(IPdfStreamWriter writer)
    {
        if (isJpeg)
        {
            var (dictionary, data) = ImageXObjects.Jpeg(picture);
            return writer.WriteToken(new StreamToken(dictionary, data));
        }

        var (image, pixels, maskDictionary, maskData) = ImageXObjects.Png(Images.Png.Png.Open(picture), withMask: true);
        if (maskDictionary is not null)
        {
            image = image.With(NameToken.Smask, writer.WriteToken(new StreamToken(maskDictionary, maskData!)));
        }

        return writer.WriteToken(new StreamToken(image, pixels));
    }
}
