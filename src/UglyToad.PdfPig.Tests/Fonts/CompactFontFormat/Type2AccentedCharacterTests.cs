namespace UglyToad.PdfPig.Tests.Fonts.CompactFontFormat
{
    using System;
    using PdfPig.Core;
    using PdfPig.Fonts.CompactFontFormat;
    using PdfPig.Fonts.CompactFontFormat.Charsets;
    using PdfPig.Fonts.CompactFontFormat.CharStrings;

    /// <summary>
    /// An endchar with four arguments — adx ady bchar achar — draws an accented character from two others in the font
    /// (Type 2 Charstring Format, appendix C): the StandardEncoding characters bchar and achar, the accent moved by
    /// (adx, ady).
    /// </summary>
    public class Type2AccentedCharacterTests
    {
        private static readonly CompactFontFormatSubroutinesSelector NoSubroutines =
            new CompactFontFormatSubroutinesSelector(null, null);

        // u: 0 0 rmoveto 100 0 rlineto 0 100 rlineto endchar.
        private const string U = "8B 8B 15 EF 8B 05 8B EF 05 0E";

        // dieresis: 10 10 rmoveto 20 0 rlineto 0 20 rlineto endchar.
        private const string Dieresis = "95 95 15 9F 8B 05 8B 9F 05 0E";

        // udieresis: 30 120 117 200 endchar — 117 is u and 200 dieresis in StandardEncoding.
        private const string UDieresis = "A9 F7 0C F7 09 F7 5C 0E";

        // The same, its width (50 past nominal) first: 50 30 120 117 200 endchar.
        private const string UDieresisWithWidth = "BD A9 F7 0C F7 09 F7 5C 0E";

        private static Type2CharStrings Font() => Type2CharStringParser.Parse(
            [Bytes("0E"), Bytes(U), Bytes(Dieresis), Bytes(UDieresis), Bytes(UDieresisWithWidth)],
            NoSubroutines,
            new Names(".notdef", "u", "dieresis", "udieresis", "uni00FC"));

        [Fact]
        public void IsDrawnAsTheBaseAndTheAccentMovedByItsOffset()
        {
            var glyph = Font().Generate("udieresis", 0, 0);

            Assert.Equal(2, glyph.Path.Count);

            var whole = PdfSubpath.GetBoundingRectangle(glyph.Path)!.Value;
            Assert.Equal(0, whole.Left, 3);
            Assert.Equal(0, whole.Bottom, 3);
            Assert.Equal(100, whole.Right, 3);
            Assert.Equal(150, whole.Top, 3);

            var accent = glyph.Path[1].GetBoundingRectangle()!.Value;
            Assert.Equal(40, accent.Left, 3);
            Assert.Equal(130, accent.Bottom, 3);
        }

        [Fact]
        public void TakesItsWidthFromAFifthArgument_NotFromTheFour()
        {
            var font = Font();

            Assert.Equal(550, font.Generate("uni00FC", 0, 500).Width!.Value, 3);
            Assert.Equal(2, font.Generate("uni00FC", 0, 500).Path.Count);

            // Four arguments are the accented character alone: no width is said, so the font's default stands.
            Assert.Null(font.Generate("udieresis", 300, 500).Width);
        }

        private static ReadOnlyMemory<byte> Bytes(string hex) =>
            new ReadOnlyMemory<byte>(Type2CharStringParserTests.StringToByteArray(hex));

        private sealed class Names : ICompactFontFormatCharset
        {
            private readonly string[] names;

            public Names(params string[] names)
            {
                this.names = names;
            }

            public bool IsCidCharset => false;

            public string GetNameByGlyphId(int glyphId) => names[glyphId];

            public string GetNameByStringId(int stringId) => names[stringId];

            public int GetStringIdByGlyphId(int glyphId) => glyphId;

            public int GetGlyphIdByName(string characterName) => Array.IndexOf(names, characterName);
        }
    }
}
