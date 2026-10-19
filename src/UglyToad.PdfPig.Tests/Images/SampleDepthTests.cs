namespace UglyToad.PdfPig.Tests.Images
{
    using PdfPig.Graphics.Colors;
    using PdfPig.Images;

    /// <summary>
    /// A sample of any depth is read, each row starting on a byte (PDF 2.0, 8.9.3) — not only the depths an image dictionary
    /// may give, since a <c>JPXDecode</c> image's is its codestream's, and 12 bits is common.
    /// </summary>
    public class SampleDepthTests
    {
        [Fact]
        public void TwelveBitSamplesAreReadAtTheirEightMostSignificantBits()
        {
            // Two rows of three twelve-bit samples, 36 bits padded to five bytes each:
            // 0xFFF 0x800 0x010, then 0x000 0x7FF 0xABC.
            byte[] data = [0xFF, 0xF8, 0x00, 0x01, 0x00, 0x00, 0x07, 0xFF, 0xAB, 0xC0];

            var samples = ColorSpaceDetailsByteConverter.Convert(DeviceGrayColorSpaceDetails.Instance, data, 12, 3, 2).ToArray();

            Assert.Equal([255, 128, 1, 0, 127, 171], samples);
        }

        [Fact]
        public void TwelveBitColourIsReadAComponentAtATime()
        {
            // One pixel of three twelve-bit components, 36 bits padded to five bytes: 0xFFF 0x640 0x400.
            byte[] data = [0xFF, 0xF6, 0x40, 0x40, 0x00];

            var samples = ColorSpaceDetailsByteConverter.Convert(DeviceRgbColorSpaceDetails.Instance, data, 12, 1, 1).ToArray();

            Assert.Equal([255, 100, 64], samples);
        }

        [Fact]
        public void SamplesNarrowerThanAByteThatCrossBytesAreStretchedToOne()
        {
            // Two rows of three three-bit samples, nine bits padded to two bytes each: 7 0 4, then 0 7 1.
            byte[] data = [0b1110_0010, 0b0000_0000, 0b0001_1100, 0b1000_0000];

            var samples = ColorSpaceDetailsByteConverter.Convert(DeviceGrayColorSpaceDetails.Instance, data, 3, 3, 2).ToArray();

            Assert.Equal([255, 0, 146, 0, 255, 36], samples);
        }

        [Fact]
        public void SixteenBitSamplesAreReadAtTheirHighByte()
        {
            byte[] data = [0xFF, 0xFF, 0x80, 0x7F, 0x00, 0xFF];

            var samples = ColorSpaceDetailsByteConverter.Convert(DeviceGrayColorSpaceDetails.Instance, data, 16, 3, 1).ToArray();

            Assert.Equal([255, 128, 0], samples);
        }

        [Fact]
        public void RowsTheDataStopsShortOfAreBlank()
        {
            // Two rows of two twelve-bit samples, three bytes each, of which the data holds the first and a sample of the second.
            byte[] data = [0xFF, 0xF8, 0x00, 0x40, 0x00];

            var samples = ColorSpaceDetailsByteConverter.Convert(DeviceGrayColorSpaceDetails.Instance, data, 12, 2, 2).ToArray();

            Assert.Equal([255, 128, 64, 0], samples);
        }
    }
}
