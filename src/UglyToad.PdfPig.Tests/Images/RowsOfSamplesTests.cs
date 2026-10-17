namespace UglyToad.PdfPig.Tests.Images
{
    using PdfPig.Graphics.Colors;
    using PdfPig.Images;

    /// <summary>
    /// An image's rows are as long as its width says, packed and padded to a whole byte where its samples are narrower than
    /// one (PDF 2.0, 8.9.3) — whatever length its data happens to be.
    /// </summary>
    public class RowsOfSamplesTests
    {
        [Fact]
        public void DataPastTheLastRowIsIgnored()
        {
            // Two rows of twelve one-bit samples, two bytes each, then a row the image's Height does not count.
            byte[] data = [0b1010_1010, 0b1010_0000, 0b1111_0000, 0b0000_0000, 0xFF, 0xFF];

            var samples = ColorSpaceDetailsByteConverter.Convert(DeviceGrayColorSpaceDetails.Instance, data, 1, 12, 2).ToArray();

            Assert.Equal(24, samples.Length);
            Assert.Equal([255, 0, 255, 0, 255, 0, 255, 0, 255, 0, 255, 0], samples[..12]);
            Assert.Equal([255, 255, 255, 255, 0, 0, 0, 0, 0, 0, 0, 0], samples[12..]);
        }

        [Fact]
        public void RowsTheDataStopsShortOfAreBlank()
        {
            // Three rows of eight eight-bit samples, of which the data holds one and a half.
            byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

            var samples = ColorSpaceDetailsByteConverter.Convert(DeviceGrayColorSpaceDetails.Instance, data, 8, 8, 3).ToArray();

            Assert.Equal(24, samples.Length);
            Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8], samples[..8]);
            Assert.Equal([9, 10, 11, 12, 0, 0, 0, 0], samples[8..16]);
            Assert.All(samples[16..], static s => Assert.Equal(0, s));
        }
    }
}
