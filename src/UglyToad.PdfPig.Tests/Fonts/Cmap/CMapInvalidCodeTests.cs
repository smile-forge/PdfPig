namespace UglyToad.PdfPig.Tests.Fonts.Cmap
{
    using System.Collections.Generic;
    using PdfFonts.Cmap;
    using UglyToad.PdfPig.Core;
    using Xunit;

    /// <summary>
    /// A code no codespace range holds — a UTF-16 surrogate in a UCS-2 CMap, whose ranges leave D800–DFFF out, as an emoji
    /// written into a Chinese font's string is — is read as long as the shortest range from the byte it begins with, so
    /// every code after it keeps its place rather than each being read a byte late.
    /// </summary>
    public class CMapInvalidCodeTests
    {
        [Fact]
        public void ACodeNoRangeHolds_IsReadFromItsFirstByte_AndTheCodesAfterItKeepTheirPlaces()
        {
            Assert.True(CMapCache.TryGet("UniCNS-UCS2-H", StackDepthGuard.Infinite, out var cmap));
            var bytes = new MemoryInputBytes(new byte[] { 0xD8, 0x3D, 0xDC, 0x76, 0x00, 0x42, 0x00, 0x6F });

            var codes = new List<int>();
            while (bytes.MoveNext())
            {
                codes.Add(cmap.ReadCode(bytes, useLenientParsing: true));
            }

            Assert.Equal(new[] { 0xD83D, 0xDC76, 0x0042, 0x006F }, codes);
        }
    }
}
