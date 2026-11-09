namespace UglyToad.PdfPig.Writer
{
    using System.Collections.Generic;
    using System.IO;
    using Images;
    using Images.Png;
    using Tokens;

    /// <summary>
    /// A picture file as the image XObject that draws it: a JPEG written as it is, a PNG's pixels compressed and its alpha
    /// channel a soft mask of its own.
    /// </summary>
    internal static class ImageXObjects
    {
        /// <summary>A JPEG's image dictionary, the stream's bytes the file's as they are.</summary>
        public static (DictionaryToken Dictionary, byte[] Data) Jpeg(byte[] jpeg)
        {
            JpegInformation info;
            using (var stream = new MemoryStream(jpeg))
            {
                info = JpegHandler.GetInformation(stream);
            }

            NameToken colorSpace;
            if (info.NumberOfComponents == 1)
            {
                colorSpace = NameToken.Devicegray;
            }
            else if (info.NumberOfComponents == 4)
            {
                colorSpace = NameToken.Devicecmyk;
            }
            else
            {
                colorSpace = NameToken.Devicergb;
            }

            var dictionary = new Dictionary<NameToken, IToken>
            {
                {NameToken.Type, NameToken.Xobject },
                {NameToken.Subtype, NameToken.Image },
                {NameToken.Width, new NumericToken(info.Width) },
                {NameToken.Height, new NumericToken(info.Height) },
                {NameToken.BitsPerComponent, new NumericToken(info.BitsPerComponent)},
                {NameToken.ColorSpace, colorSpace},
                {NameToken.Filter, NameToken.DctDecode},
                {NameToken.Length, new NumericToken(jpeg.Length)}
            };

            return (new DictionaryToken(dictionary), jpeg);
        }

        /// <summary>
        /// A PNG's image dictionary and its pixels compressed; and, where it has an alpha channel and
        /// <paramref name="withMask"/>, that channel as a soft mask, for the caller to write and name as the image's
        /// <c>/SMask</c>.
        /// </summary>
        public static (DictionaryToken Dictionary, byte[] Data, DictionaryToken? MaskDictionary, byte[]? MaskData) Png(Png png, bool withMask)
        {
            var data = new byte[png.Width * png.Height * 3];
            int pixelIndex = 0;

            for (var rowIndex = 0; rowIndex < png.Height; rowIndex++)
            {
                for (var colIndex = 0; colIndex < png.Width; colIndex++)
                {
                    var pixel = png.GetPixel(colIndex, rowIndex);

                    data[pixelIndex++] = pixel.R;
                    data[pixelIndex++] = pixel.G;
                    data[pixelIndex++] = pixel.B;
                }
            }

            var widthToken = new NumericToken(png.Width);
            var heightToken = new NumericToken(png.Height);

            DictionaryToken? maskDictionary = null;
            byte[]? maskData = null;

            if (png.HasAlphaChannel && withMask)
            {
                var smaskData = new byte[data.Length / 3];
                for (var rowIndex = 0; rowIndex < png.Height; rowIndex++)
                {
                    for (var colIndex = 0; colIndex < png.Width; colIndex++)
                    {
                        var pixel = png.GetPixel(colIndex, rowIndex);

                        var index = rowIndex * png.Width + colIndex;
                        smaskData[index] = pixel.A;
                    }
                }

                maskData = DataCompressor.CompressBytes(smaskData);

                maskDictionary = new DictionaryToken(new Dictionary<NameToken, IToken>
                {
                    {NameToken.Type, NameToken.Xobject},
                    {NameToken.Subtype, NameToken.Image},
                    {NameToken.Width, widthToken},
                    {NameToken.Height, heightToken},
                    {NameToken.ColorSpace, NameToken.Devicegray},
                    {NameToken.BitsPerComponent, new NumericToken(8)},
                    {NameToken.Decode, new ArrayToken(new IToken[] { new NumericToken(0), new NumericToken(1) })},
                    {NameToken.Length, new NumericToken(maskData.Length)},
                    {NameToken.Filter, NameToken.FlateDecode}
                });
            }

            var compressed = DataCompressor.CompressBytes(data);

            var dictionary = new Dictionary<NameToken, IToken>
            {
                {NameToken.Type, NameToken.Xobject},
                {NameToken.Subtype, NameToken.Image},
                {NameToken.Width, widthToken},
                {NameToken.Height, heightToken},
                {NameToken.BitsPerComponent, new NumericToken(8)},
                {NameToken.ColorSpace, NameToken.Devicergb},
                {NameToken.Filter, NameToken.FlateDecode},
                {NameToken.Length, new NumericToken(compressed.Length)}
            };

            return (new DictionaryToken(dictionary), compressed, maskDictionary, maskData);
        }
    }
}
