namespace UglyToad.PdfPig.Content
{
    using System.Collections.Generic;
    using Tokens;

    /// <summary>
    /// Pdf image extensions.
    /// </summary>
    public static class PdfImageExtensions
    {
        /// <summary>
        /// <c>true</c> if the image colors needs to be reversed based on the Decode array and color space. <c>false</c> otherwise.
        /// </summary>
        public static bool NeedsReverseDecode(this IPdfImage pdfImage)
        {
            return pdfImage.Decode?.Count >= 2 && pdfImage.Decode[0] == 1 && pdfImage.Decode[1] == 0;
        }

        /// <summary>
        /// The colour a soft mask's parent image was preblended with, its <c>Matte</c> entry (PDF 2.0, 11.6.5.3): one component
        /// for each of the parent image's colour space. <c>null</c> when the soft mask has none, or one that is not all numbers.
        /// </summary>
        public static IReadOnlyList<double>? GetMatte(this IPdfImage softMask)
        {
            if (!softMask.ImageDictionary.TryGet(NameToken.Matte, out ArrayToken? matte) || matte is null)
            {
                return null;
            }

            var colour = new double[matte.Length];
            for (var i = 0; i < colour.Length; i++)
            {
                if (matte[i] is not NumericToken component)
                {
                    return null;
                }

                colour[i] = component.Double;
            }

            return colour;
        }
    }
}
