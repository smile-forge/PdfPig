namespace UglyToad.PdfPig.Tests.Images
{
    using System;
    using PdfPig.Graphics.Colors;
    using PdfPig.Graphics.Core;
    using PdfPig.Images;
    using Xunit;

    /// <summary>
    /// An image preblended with its soft mask's Matte colour (11.6.5.3) has the matte taken back out of it after its
    /// Decode array and before its colour space transform, c = m + (c' - m) / a.
    /// <para>
    /// The samples are written at an opacity of 85, a third, against a white matte, so each preblended component is
    /// exactly 255 - (255 - c) / 3 and its true colour comes back without rounding.
    /// </para>
    /// </summary>
    public class SoftMaskMatteTests
    {
        private static readonly double[] WhiteMatte = [1.0, 1.0, 1.0];

        private static byte[] Convert(ColorSpaceDetails details, byte[] samples, int width, double[]? decode,
            double[]? matte, byte[] opacity, int bitsPerComponent = 8)
        {
            return ColorSpaceDetailsByteConverter.Convert(details, samples, bitsPerComponent, width, 1, decode,
                RenderingIntent.RelativeColorimetric, matte, opacity).ToArray();
        }

        [Fact]
        public void PreblendedRgb_DrawsItsTrueColour()
        {
            // Green and black, a third opaque, preblended with white.
            byte[] samples = [170, 255, 170, 170, 170, 170];

            var rgb = Convert(DeviceRgbColorSpaceDetails.Instance, samples, 2, null, WhiteMatte, [85, 85]);

            Assert.Equal(new byte[] { 0, 255, 0, 0, 0, 0 }, rgb);
        }

        [Fact]
        public void OpaqueAndTransparentSamples_AreLeftAsTheyAre()
        {
            byte[] samples = [10, 20, 30, 40, 50, 60];

            var rgb = Convert(DeviceRgbColorSpaceDetails.Instance, samples, 2, null, WhiteMatte, [255, 0]);

            Assert.Equal(new byte[] { 10, 20, 30, 40, 50, 60 }, rgb);
        }

        [Fact]
        public void WithoutAMatte_TheSoftMaskDoesNotTouchTheColour()
        {
            byte[] samples = [170, 255, 170];

            var rgb = Convert(DeviceRgbColorSpaceDetails.Instance, samples, 1, null, null, [85]);

            Assert.Equal(new byte[] { 170, 255, 170 }, rgb);
        }

        [Fact]
        public void TheMatteIsTakenOutAfterTheDecodeArray()
        {
            // An inverted Decode: the preblended green 170 255 170 is written as 85 0 85.
            byte[] samples = [85, 0, 85];

            var rgb = Convert(DeviceRgbColorSpaceDetails.Instance, samples, 1, [1.0, 0.0, 1.0, 0.0, 1.0, 0.0],
                WhiteMatte, [85]);

            Assert.Equal(new byte[] { 0, 255, 0 }, rgb);
        }

        [Fact]
        public void SamplesNarrowerThanAByte_AreUnmixedOnceStretched()
        {
            // 4 bits a sample, two pixels to the three bytes: 10 of 15 is 170 of 255, and 15 is 255.
            byte[] samples = [0xAF, 0xAA, 0xFA];

            var rgb = Convert(DeviceRgbColorSpaceDetails.Instance, samples, 2, null, WhiteMatte, [85, 85], bitsPerComponent: 4);

            Assert.Equal(new byte[] { 0, 255, 0, 0, 255, 0 }, rgb);
        }

        [Fact]
        public void PreblendedGrey_UsesTheMatteInTheImagesOwnSpace()
        {
            // Black, a third opaque, preblended with a mid grey matte of 0.6 (153): 153 - 153 / 3 = 102.
            byte[] samples = [102];

            var grey = Convert(DeviceGrayColorSpaceDetails.Instance, samples, 1, null, [0.6], [85]);

            Assert.Equal(new byte[] { 0 }, grey);
        }

        [Fact]
        public void PreblendedIndexed_IsUnmixedThroughItsColourTable()
        {
            // Entry 0 is the white matte, entry 1 the preblended green; the matte names its colour by index.
            var indexed = new IndexedColorSpaceDetails(DeviceRgbColorSpaceDetails.Instance, 1,
                [255, 255, 255, 170, 255, 170]);

            var rgb = Convert(indexed, [1, 0], 2, null, [0.0], [85, 85]);

            Assert.Equal(new byte[] { 0, 255, 0, 255, 255, 255 }, rgb);
        }

        [Fact]
        public void AMatteWithTooFewComponents_IsNotAColourOfTheSpace()
        {
            byte[] samples = [170, 255, 170];

            var rgb = Convert(DeviceRgbColorSpaceDetails.Instance, samples, 1, null, [1.0], [85]);

            Assert.Equal(new byte[] { 170, 255, 170 }, rgb);
        }

        [Fact]
        public void TheImagesOwnSamples_AreNotUnmixedInPlace()
        {
            // Image bytes are kept for the next caller, which would otherwise take the matte out a second time.
            byte[] samples = [170, 255, 170];

            Convert(DeviceRgbColorSpaceDetails.Instance, samples, 1, null, WhiteMatte, [85]);
            var again = Convert(DeviceRgbColorSpaceDetails.Instance, samples, 1, null, WhiteMatte, [85]);

            Assert.Equal(new byte[] { 170, 255, 170 }, samples);
            Assert.Equal(new byte[] { 0, 255, 0 }, again);
        }
    }
}
