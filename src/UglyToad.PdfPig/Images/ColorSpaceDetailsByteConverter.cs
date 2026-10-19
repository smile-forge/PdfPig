namespace UglyToad.PdfPig.Images
{
    using Content;
    using Graphics.Colors;
    using System;
    using System.Collections.Generic;
    using UglyToad.PdfPig.Graphics.Core;

    /// <summary>
    /// Utility for working with the bytes in <see cref="IPdfImage"/>s and converting according to their <see cref="ColorSpaceDetails"/>.s
    /// </summary>
    public static class ColorSpaceDetailsByteConverter
    {
        /// <summary>
        /// Converts the output bytes (if available) of <see cref="IPdfImage.TryGetBytesAsMemory"/>
        /// to actual pixel values using the <see cref="IPdfImage.ColorSpaceDetails"/>. For most images this doesn't
        /// change the data but for <see cref="ColorSpace.Indexed"/> it will convert the bytes which are indexes into the
        /// real pixel data into the real pixel data.
        /// </summary>
        public static Span<byte> Convert(ColorSpaceDetails details, Span<byte> decoded, int bitsPerComponent, int imageWidth, int imageHeight)
        {
            return Convert(details, decoded, bitsPerComponent, imageWidth, imageHeight, null, RenderingIntent.RelativeColorimetric);
        }

        /// <summary>
        /// Converts the output bytes (if available) of <see cref="IPdfImage.TryGetBytesAsMemory"/>
        /// to actual pixel values using the <see cref="IPdfImage.ColorSpaceDetails"/>, applying the image's
        /// <paramref name="decode"/> array before the colour space transform.
        /// </summary>
        /// <remarks>
        /// The Decode array (PDF 2.0, 8.9.5.10) maps each raw sample value to a value in the colour-space range
        /// before the colour space transform is applied. An empty or null <paramref name="decode"/> means "use the
        /// colour space default" (which is a no-op for the common case).
        /// </remarks>
        public static Span<byte> Convert(ColorSpaceDetails details, Span<byte> decoded, int bitsPerComponent, int imageWidth, int imageHeight,
            IReadOnlyList<double>? decode, RenderingIntent intent)
        {
            if (decoded.IsEmpty)
            {
                return [];
            }

            if (details is null)
            {
                return decoded;
            }

            // A row holds as many samples as the image is wide, packed and padded to a whole byte where they do not
            // fill one (PDF 2.0, 8.9.3): its length comes from the width, never from how much data there is, which
            // can run on past the last row (a scanner's whole page under a shorter Height, issue #1183) or stop short
            // of it (PDFBOX-492-4.jar-8.pdf, page 2). Samples other than a byte wide are unpacked a byte each.
            var components = details.NumberOfColorComponents;
            var rowLength = imageWidth * components;
            decoded = bitsPerComponent == 8
                ? Rows(decoded, rowLength, imageHeight)
                : UnpackRows(decoded, bitsPerComponent, rowLength, imageHeight);

            ApplyDecode(decoded, details, decode, bitsPerComponent);

            return details.Transform(decoded, intent);
        }

        private static void ApplyDecode(Span<byte> samples, ColorSpaceDetails details, IReadOnlyList<double>? decode, int bitsPerComponent)
        {
            if (bitsPerComponent > 8)
            {
                // bpc passed to ApplyDecode reflects the *post-unpack* sample range:
                //   - a sample wider than a byte is reduced to its eight most significant bits inside UnpackRows.
                //   - sub-8bpc samples remain in [0, 2^bpc - 1] after unpacking.
                bitsPerComponent = 8;
            }

            int components = details.NumberOfColorComponents;
            int sampleMax = (1 << bitsPerComponent) - 1;
            if (sampleMax <= 0)
            {
                return;
            }

            Span<double> defaults = components <= 32 ? stackalloc double[2 * components] : new double[2 * components];
            details.GetDefaultDecode(bitsPerComponent, defaults);

            bool isIndexed = details.Type == ColorSpace.Indexed;
            bool hasDecode = decode is not null && decode.Count >= components * 2;

            // Fast path: the byte would come out as it went in.
            //   - Indexed: the default Decode is the identity at any bit depth (S -> S).
            //   - Non-Indexed 8bpc: the sample already IS the position within the default range, which is
            //     exactly what a byte encodes (see below), so the round trip cancels.
            // Sub-8bpc still has to be stretched from [0, 2^bpc - 1] to [0, 255].
            if (!hasDecode || IsDefaultDecode(decode!, components, defaults))
            {
                if (isIndexed || bitsPerComponent == 8)
                {
                    return;
                }
            }

            // Per PDF 2.0 8.9.5.10, for each component c with raw sample S in [0, sampleMax]:
            //     x_c = Dmin_c + S * (Dmax_c - Dmin_c) / sampleMax
            //
            // What the byte then holds differs by colour space:
            //   - Indexed: x_c is the palette index itself, kept as-is in [0, sampleMax].
            //   - Non-Indexed: a byte cannot hold an L* of 100 or a negative a*, so it holds x_c's
            //     POSITION within the component's default range, scaled to [0, 255]. The colour space
            //     reverses this in its Transform via DecodeRawComponents, so the pair round-trips. For the
            //     usual [0, 1] space position and value coincide and this is the historical behaviour.
            for (int i = 0; i < samples.Length; i++)
            {
                int c = i % components;
                double defaultMin = defaults[c * 2];
                double defaultMax = defaults[c * 2 + 1];

                double dMin = hasDecode ? decode![c * 2] : defaultMin;
                double dMax = hasDecode ? decode![c * 2 + 1] : defaultMax;
                double x = dMin + samples[i] * (dMax - dMin) / sampleMax;

                double outputScale;
                int outputMax;
                if (isIndexed)
                {
                    outputScale = 1.0;
                    outputMax = sampleMax;
                }
                else
                {
                    double span = defaultMax - defaultMin;

                    // A degenerate range (Lab /Range [0 0 0 0] is legal) pins every sample to the single
                    // valid value, which is position zero.
                    x = span == 0.0 ? 0.0 : (x - defaultMin) / span;
                    outputScale = 255.0;
                    outputMax = 255;
                }

                int rounded = (int)Math.Round(x * outputScale, MidpointRounding.AwayFromZero);
                if (rounded < 0)
                {
                    rounded = 0;
                }
                else if (rounded > outputMax)
                {
                    rounded = outputMax;
                }

                samples[i] = (byte)rounded;
            }
        }

        private static bool IsDefaultDecode(IReadOnlyList<double> decode, int components, ReadOnlySpan<double> defaults)
        {
            for (int c = 0; c < components; c++)
            {
                if (decode[c * 2] != defaults[c * 2] || decode[c * 2 + 1] != defaults[c * 2 + 1])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The image's rows, <paramref name="rowLength"/> samples each, out of <paramref name="input"/>'s rows of
        /// <paramref name="bitsPerComponent"/>-bit samples, packed most significant bit first, each row starting on a byte
        /// (PDF 2.0, 8.9.3): a sample a byte, one wider than a byte kept to its eight most significant bits. Rows the data stops
        /// short of are left at nought, and data past the last row is ignored.
        /// </summary>
        /// <remarks>
        /// Any depth is read alike, not only those an image dictionary may give: a <c>JPXDecode</c> image's is its codestream's
        /// (PDF 2.0, 8.9.5), and 12 bits is common.
        /// </remarks>
        private static Span<byte> UnpackRows(ReadOnlySpan<byte> input, int bitsPerComponent, int rowLength, int rows)
        {
            if (bitsPerComponent is < 1 or > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(bitsPerComponent), bitsPerComponent, "A sample is from 1 to 32 bits.");
            }

            int rowBytes = (int)(((long)rowLength * bitsPerComponent + 7) / 8);
            int narrowing = Math.Max(bitsPerComponent - 8, 0);
            ulong mask = (1UL << bitsPerComponent) - 1;

            Span<byte> unpacked = new byte[rowLength * rows];
            for (int row = 0; row < rows; row++)
            {
                int from = row * rowBytes;
                if (from >= input.Length)
                {
                    break;
                }

                ReadOnlySpan<byte> source = input.Slice(from, Math.Min(rowBytes, input.Length - from));
                Span<byte> target = unpacked.Slice(row * rowLength, rowLength);

                // Whole bytes go into the accumulator at the bottom and samples leave it from the top. Fewer bits than a sample
                // are ever left over, so it never holds more than 39 that are still to be read.
                ulong held = 0;
                int count = 0;
                int at = 0;
                for (int i = 0; i < target.Length; i++)
                {
                    while (count < bitsPerComponent)
                    {
                        if (at == source.Length)
                        {
                            return unpacked;
                        }

                        held = (held << 8) | source[at++];
                        count += 8;
                    }

                    count -= bitsPerComponent;
                    target[i] = (byte)(((held >> count) & mask) >> narrowing);
                }
            }

            return unpacked;
        }

        /// <summary>
        /// The image's rows, <paramref name="rowLength"/> samples each, out of samples a byte each: data past the last row
        /// ignored, and rows the data stops short of left at nought.
        /// </summary>
        private static Span<byte> Rows(Span<byte> samples, int rowLength, int rows)
        {
            var size = rowLength * rows;
            if (samples.Length >= size)
            {
                return samples.Slice(0, size);
            }

            Span<byte> result = new byte[size];
            samples.CopyTo(result);
            return result;
        }
    }
}
