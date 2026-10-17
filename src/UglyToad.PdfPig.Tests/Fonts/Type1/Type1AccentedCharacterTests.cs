namespace UglyToad.PdfPig.Tests.Fonts.Type1
{
    using PdfPig.Core;
    using PdfPig.Fonts.Type1.CharStrings;
    using PdfPig.Fonts.Type1.CharStrings.Commands;
    using PdfPig.Fonts.Type1.CharStrings.Commands.PathConstruction;
    using PdfPig.Fonts.Type1.CharStrings.Commands.StartFinishOutline;
    using Command = PdfPig.Core.Union<double, PdfPig.Fonts.Type1.CharStrings.Commands.LazyType1Command>;

    /// <summary>
    /// A seac — asb adx ady bchar achar — draws an accented character from two others in the font: the StandardEncoding
    /// characters bchar and achar, the base where it stands and the accent's origin placed at (adx, ady) from this
    /// character's left sidebearing, less the accent's own sidebearing asb, which its outline already holds (Adobe Type 1
    /// Font Format, 6.4).
    /// </summary>
    public class Type1AccentedCharacterTests
    {
        private static Type1CharStrings Font() => new Type1CharStrings(
            new Dictionary<string, Type1CharStrings.CommandSequence>
            {
                // u: 20 500 hsbw, 0 0 rmoveto 100 0 rlineto 0 100 rlineto — from its sidebearing at (20, 0).
                ["u"] = Sequence(V(20), V(500), C(HsbwCommand.Lazy), V(0), V(0), C(RMoveToCommand.Lazy),
                    V(100), V(0), C(RLineToCommand.Lazy), V(0), V(100), C(RLineToCommand.Lazy), C(EndCharCommand.Lazy)),

                // dieresis: 30 300 hsbw, 0 0 rmoveto 20 0 rlineto 0 20 rlineto — from (30, 0).
                ["dieresis"] = Sequence(V(30), V(300), C(HsbwCommand.Lazy), V(0), V(0), C(RMoveToCommand.Lazy),
                    V(20), V(0), C(RLineToCommand.Lazy), V(0), V(20), C(RLineToCommand.Lazy), C(EndCharCommand.Lazy)),

                // udieresis: 20 500 hsbw, 30 70 140 117 200 seac — 117 is u and 200 dieresis in StandardEncoding.
                ["udieresis"] = Sequence(V(20), V(500), C(HsbwCommand.Lazy),
                    V(30), V(70), V(140), V(117), V(200), C(SeacCommand.Lazy)),
            },
            new Dictionary<int, string>(),
            new Dictionary<int, Type1CharStrings.CommandSequence>());

        [Fact]
        public void IsDrawnAsTheBaseAndTheAccentPlacedAtItsOrigin()
        {
            Assert.True(Font().TryGenerate("udieresis", out var path));

            Assert.Equal(2, path.Count);

            var baseCharacter = path[0].GetBoundingRectangle()!.Value;
            Assert.Equal(20, baseCharacter.Left, 3);
            Assert.Equal(120, baseCharacter.Right, 3);
            Assert.Equal(100, baseCharacter.Top, 3);

            // The accent's outline starts at its sidebearing, 30; moved by 20 + 70 - 30 across and 140 up.
            var accent = path[1].GetBoundingRectangle()!.Value;
            Assert.Equal(90, accent.Left, 3);
            Assert.Equal(110, accent.Right, 3);
            Assert.Equal(140, accent.Bottom, 3);
            Assert.Equal(160, accent.Top, 3);
        }

        private static Type1CharStrings.CommandSequence Sequence(params Command[] commands) =>
            new Type1CharStrings.CommandSequence(commands);

        private static Command V(double value) => Command.One(value);

        private static Command C(LazyType1Command command) => Command.Two(command);
    }
}
