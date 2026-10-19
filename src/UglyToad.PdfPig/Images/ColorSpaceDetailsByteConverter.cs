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
            return Convert(details, decoded, bitsPerComponent, imageWidth, imageHeight, decode, intent, null, ReadOnlySpan<byte>.Empty);
        }

        /// <summary>
        /// Converts the output bytes (if available) of <see cref="IPdfImage.TryGetBytesAsMemory"/>
        /// to actual pixel values using the <see cref="IPdfImage.ColorSpaceDetails"/>, applying the image's
        /// <paramref name="decode"/> array and taking out the <paramref name="matte"/> colour it was preblended with
        /// before the colour space transform.
        /// </summary>
        /// <remarks>
        /// An image whose soft mask has a Matte entry (PDF 2.0, 11.6.5.3) had its colour mixed with the matte by its
        /// opacity before it was written, c' = m + a × (c − m), so drawn as it stands its soft edges carry the matte as
        /// a fringe. Here it is taken back out, c = m + (c' − m) / a, in the image's own colour space after Decode, which
        /// is where the mixing was done. An Indexed image is unmixed in its base colour space, through its colour table:
        /// an index cannot be mixed, the colour it names can.
        /// </remarks>
        /// <param name="details">The image's colour space.</param>
        /// <param name="decoded">The image's samples, as its filters leave them.</param>
        /// <param name="bitsPerComponent">The image's bits per component.</param>
        /// <param name="imageWidth">The image's width, in samples.</param>
        /// <param name="imageHeight">The image's height, in samples.</param>
        /// <param name="decode">The image's Decode array, or <c>null</c> for the colour space's default.</param>
        /// <param name="intent">The rendering intent of the colour space transform.</param>
        /// <param name="matte">The soft mask's Matte entry (<see cref="PdfImageExtensions.GetMatte"/>), a colour in
        /// <paramref name="details"/>; <c>null</c> when the image was not preblended.</param>
        /// <param name="opacity">The soft mask, one byte a sample from 0 (transparent) to 255 (opaque) after its own Decode
        /// array, on the image's grid, as a Matte requires the two to be. Not read without a <paramref name="matte"/>.</param>
        public static Span<byte> Convert(ColorSpaceDetails details, Span<byte> decoded, int bitsPerComponent, int imageWidth, int imageHeight,
            IReadOnlyList<double>? decode, RenderingIntent intent, IReadOnlyList<double>? matte, ReadOnlySpan<byte> opacity)
        {
            if (decoded.IsEmpty)
            {
                return [];
            }

            if (details is null)
            {
                return decoded;
            }

            var source = decoded;

            if (bitsPerComponent != 8)
            {
                // Unpack components such that they occupy one byte each
                decoded = UnpackComponents(decoded, bitsPerComponent);
            }

            // Remove padding bytes when the stride width differs from the image width
            var bytesPerPixel = details.NumberOfColorComponents;
            var strideWidth = decoded.Length / imageHeight / bytesPerPixel;
            if (strideWidth != imageWidth)
            {
                if (bytesPerPixel > 1 && imageWidth * imageHeight * bytesPerPixel < decoded.Length)
                {
                    // Fixed thanks to / see discussion at https://github.com/UglyToad/PdfPig/issues/1183
                    // Unclear what should be done here, we assume we can just remove the trailing bytes
                    decoded = decoded.Slice(0, imageWidth * imageHeight * bytesPerPixel);
                }
                else
                {
                    decoded = RemoveStridePadding(decoded, strideWidth, imageWidth, imageHeight, bytesPerPixel);
                }
            }

            ApplyDecode(decoded, details, decode, bitsPerComponent);

            if (matte is not null && !opacity.IsEmpty && MatteSamples(details, matte) is { } matteSamples)
            {
                if (details is IndexedColorSpaceDetails indexed)
                {
                    decoded = indexed.UnwrapIndexedColorSpaceBytes(decoded);
                    details = indexed.BaseColorSpace;
                }

                // The samples handed in may be the image's own, kept for the next caller: those are unmixed in a copy, so that
                // a second conversion does not take the matte out twice.
                if (decoded.Overlaps(source))
                {
                    decoded = decoded.ToArray();
                }

                Unblend(decoded, matteSamples, opacity);
            }

            return details.Transform(decoded, intent);
        }

        /// <summary>
        /// The matte colour as the samples are held once their Decode array is applied: each component's position within its
        /// default range, over [0, 255] — or, in an Indexed colour space, the colour table's entry for the matte's index, which
        /// is what the samples are unmixed as. <c>null</c> when the matte has too few components to be a colour of the space.
        /// </summary>
        private static double[]? MatteSamples(ColorSpaceDetails details, IReadOnlyList<double> matte)
        {
            int components = details.NumberOfColorComponents;
            if (matte.Count < components)
            {
                return null;
            }

            double[] samples;
            if (details is IndexedColorSpaceDetails indexed)
            {
                var index = matte[0] > 0 ? (byte)Math.Min(Math.Round(matte[0], MidpointRounding.AwayFromZero), byte.MaxValue) : (byte)0;
                var entry = indexed.UnwrapIndexedColorSpaceBytes(new[] { index });
                samples = new double[entry.Length];
                for (int c = 0; c < entry.Length; c++)
                {
                    samples[c] = entry[c];
                }

                return samples;
            }

            Span<double> defaults = components <= 32 ? stackalloc double[2 * components] : new double[2 * components];
            details.GetDefaultDecode(8, defaults);

            samples = new double[components];
            for (int c = 0; c < components; c++)
            {
                double min = defaults[c * 2];
                double span = defaults[c * 2 + 1] - min;
                double position = span == 0.0 ? 0.0 : (matte[c] - min) / span;
                samples[c] = Math.Min(Math.Max(position, 0.0), 1.0) * 255.0;
            }

            return samples;
        }

        /// <summary>
        /// Take the matte colour back out of samples preblended with it: c = m + (c' − m) / a, a being each sample's opacity
        /// over 255. A sample that is wholly transparent is left as it is, since none of its colour shows, and one that is
        /// wholly opaque has no matte in it.
        /// </summary>
        private static void Unblend(Span<byte> samples, ReadOnlySpan<double> matte, ReadOnlySpan<byte> opacity)
        {
            int components = matte.Length;
            int count = Math.Min(samples.Length / components, opacity.Length);
            for (int p = 0; p < count; p++)
            {
                byte a = opacity[p];
                if (a == 0 || a == byte.MaxValue)
                {
                    continue;
                }

                double scale = byte.MaxValue / (double)a;
                var sample = samples.Slice(p * components, components);
                for (int c = 0; c < components; c++)
                {
                    double value = matte[c] + (sample[c] - matte[c]) * scale;
                    sample[c] = value <= 0.0 ? (byte)0
                        : value >= byte.MaxValue ? byte.MaxValue
                        : (byte)Math.Round(value, MidpointRounding.AwayFromZero);
                }
            }
        }

        private static void ApplyDecode(Span<byte> samples, ColorSpaceDetails details, IReadOnlyList<double>? decode, int bitsPerComponent)
        {
            if (bitsPerComponent == 16)
            {
                // bpc passed to ApplyDecode reflects the *post-unpack* sample range:
                //   - 16bpc is reduced to its 8-bit high-byte equivalent inside UnpackComponents.
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

        private static Span<byte> UnpackComponents(Span<byte> input, int bitsPerComponent)
        {
            if (bitsPerComponent == 16) // Example with MOZILLA-3136-0.pdf (page 3)
            {
                int size = input.Length / 2;
                var unpacked16 = input.Slice(0, size); // In place

                for (int b = 0; b < size; ++b)
                {
                    int i = 2 * b;
                    // Convert to UInt16 and divide by 256
                    unpacked16[b] = (byte)((ushort)(input[i + 1] | input[i] << 8) / 256);
                }

                return unpacked16;
            }

            int end = 8 - bitsPerComponent;

            Span<byte> unpacked = new byte[input.Length * (int)Math.Ceiling((end + 1) / (double)bitsPerComponent)];

            int right = (int)Math.Pow(2, bitsPerComponent) - 1;

            int u = 0;
            foreach (byte b in input)
            {
                for (int i = end; i >= 0; i -= bitsPerComponent)
                {
                    unpacked[u++] = (byte)((b >> i) & right);
                }
            }

            return unpacked;
        }

        private static Span<byte> RemoveStridePadding(Span<byte> input, int strideWidth, int imageWidth, int imageHeight, int multiplier)
        {
            int size = imageWidth * imageHeight * multiplier;
            Span<byte> result = size < input.Length ? input.Slice(0, size) : new byte[size];
            // See PDFBOX-492-4.jar-8.pdf, page 2 for size > input.Length

            for (int y = 0; y < imageHeight; y++)
            {
                int sourceIndex = y * strideWidth;
                int targetIndex = y * imageWidth;
                input.Slice(sourceIndex, imageWidth).CopyTo(result.Slice(targetIndex, imageWidth));
            }

            return result;
        }
    }
}
