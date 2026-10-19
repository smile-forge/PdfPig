namespace UglyToad.PdfPig.Tests.Images
{
    using PdfPig.Graphics.Colors;
    using PdfPig.Graphics.Core;
    using PdfPig.Images;
    using Xunit;

    /// <summary>
    /// The samples handed to the converter are the image's own: <see cref="UglyToad.PdfPig.XObjects.XObjectImage"/> keeps
    /// them once read and hands the same bytes to every caller, so converting an image must leave them as they were, or
    /// a second conversion starts from colours rather than samples.
    /// </summary>
    public class ConvertLeavesSamplesTests
    {
        private static byte[] Convert(ColorSpaceDetails details, byte[] samples, int width, double[]? decode = null)
        {
            return ColorSpaceDetailsByteConverter.Convert(details, samples, 8, width, 1, decode,
                RenderingIntent.RelativeColorimetric).ToArray();
        }

        [Fact]
        public void AnImageUnderAnInvertedDecode_ConvertsAlikeTwice()
        {
            byte[] samples = [0, 64, 255];
            double[] inverted = [1.0, 0.0, 1.0, 0.0, 1.0, 0.0];

            var first = Convert(DeviceRgbColorSpaceDetails.Instance, samples, 1, inverted);
            var second = Convert(DeviceRgbColorSpaceDetails.Instance, samples, 1, inverted);

            Assert.Equal(new byte[] { 255, 191, 0 }, first);
            Assert.Equal(first, second);
            Assert.Equal(new byte[] { 0, 64, 255 }, samples);
        }

        [Fact]
        public void AnIndexedImageOverGrey_ConvertsAlikeTwice()
        {
            // One colour byte an index, so the lookup could be made where the indices lie.
            var indexed = new IndexedColorSpaceDetails(DeviceGrayColorSpaceDetails.Instance, 1, [200, 50]);
            byte[] samples = [0, 1];

            var first = Convert(indexed, samples, 2);
            var second = Convert(indexed, samples, 2);

            Assert.Equal(new byte[] { 200, 50 }, first);
            Assert.Equal(first, second);
            Assert.Equal(new byte[] { 0, 1 }, samples);
        }

        [Fact]
        public void AnIndexPastHiVal_IsReadAsHiValAndLeftAsWritten()
        {
            var indexed = new IndexedColorSpaceDetails(DeviceRgbColorSpaceDetails.Instance, 1,
                [0, 0, 0, 255, 0, 0]);
            byte[] samples = [5];

            var rgb = Convert(indexed, samples, 1);

            Assert.Equal(new byte[] { 255, 0, 0 }, rgb);
            Assert.Equal(new byte[] { 5 }, samples);
        }
    }
}
