namespace UglyToad.PdfPig.Tests.ContentTests
{
    using System.Collections.Generic;
    using System.Linq;
    using PdfPig.Annotations;
    using PdfPig.Core;
    using PdfPig.Logging;
    using PdfPig.Tokens;
    using PdfPig.Tests.Tokens;
    using Xunit;

    /// <summary>
    /// An annotation's appearance dictionary, and an appearance given as a dictionary of states, may each be written in
    /// place or by reference (12.5.5) — Apple's Preview writes a stamp's <c>/AP</c> by reference — and read the same.
    /// </summary>
    public class AnnotationAppearanceReferenceTests
    {
        private static readonly IndirectReference Appearances = new IndirectReference(20, 0);
        private static readonly IndirectReference States = new IndirectReference(21, 0);
        private static readonly IndirectReference Normal = new IndirectReference(22, 0);
        private static readonly IndirectReference Off = new IndirectReference(23, 0);

        [Fact]
        public void AnAppearanceDictionaryGivenByReference_GivesItsNormalAppearance()
        {
            var scanner = Scanner();
            scanner.Objects[Appearances] = Object(Appearances, Dictionary(("N", new IndirectReferenceToken(Normal))));

            var annotation = Read(scanner, ("AP", new IndirectReferenceToken(Appearances)));

            Assert.True(annotation.HasNormalAppearance);
            Assert.True(annotation.NormalAppearance!.IsStateless);
            Assert.Same(scanner.Objects[Normal].Data, annotation.NormalAppearance.StatelessAppearanceStream);
        }

        [Fact]
        public void AnAppearanceOfStatesGivenByReference_GivesEachState()
        {
            var scanner = Scanner();
            scanner.Objects[States] = Object(States, Dictionary(("On", new IndirectReferenceToken(Normal)), ("Off", new IndirectReferenceToken(Off))));

            var annotation = Read(scanner,
                ("AP", Dictionary(("N", new IndirectReferenceToken(States)))),
                ("AS", NameToken.Create("Off")));

            Assert.True(annotation.HasNormalAppearance);
            Assert.False(annotation.NormalAppearance!.IsStateless);
            Assert.Equal(new[] { "Off", "On" }, annotation.NormalAppearance.GetStates.OrderBy(static s => s));
            Assert.Same(scanner.Objects[Off].Data, annotation.NormalAppearance.Get("Off"));
            Assert.Equal("Off", annotation.AppearanceState);
        }

        [Fact]
        public void AnAppearanceDictionaryInPlace_IsReadAsBefore()
        {
            var scanner = Scanner();

            var annotation = Read(scanner, ("AP", Dictionary(("N", new IndirectReferenceToken(Normal)))));

            Assert.True(annotation.HasNormalAppearance);
            Assert.Same(scanner.Objects[Normal].Data, annotation.NormalAppearance!.StatelessAppearanceStream);
        }

        private static Annotation Read(TestPdfTokenScanner scanner, params (string Key, IToken Value)[] entries)
        {
            var annotation = new Dictionary<NameToken, IToken>
            {
                { NameToken.Type, NameToken.Annot },
                { NameToken.Subtype, NameToken.Create("Stamp") },
                { NameToken.Rect, new ArrayToken(new IToken[] { new NumericToken(0), new NumericToken(0), new NumericToken(595), new NumericToken(842) }) },
            };
            foreach (var (key, value) in entries)
            {
                annotation[NameToken.Create(key)] = value;
            }

            var page = new DictionaryToken(new Dictionary<NameToken, IToken>
            {
                { NameToken.Annots, new ArrayToken(new IToken[] { new DictionaryToken(annotation) }) }
            });

            return new AnnotationProvider(scanner, page, TransformationMatrix.Identity, null!, new NoOpLog()).GetAnnotations().Single();
        }

        private static TestPdfTokenScanner Scanner()
        {
            var scanner = new TestPdfTokenScanner();
            foreach (var reference in new[] { Normal, Off })
            {
                scanner.Objects[reference] = Object(reference, new StreamToken(Dictionary(
                    ("Type", NameToken.Create("XObject")), ("Subtype", NameToken.Create("Form"))), new byte[] { (byte)'q', (byte)' ', (byte)'Q' }));
            }

            return scanner;
        }

        private static ObjectToken Object(IndirectReference reference, IToken data) => new ObjectToken(XrefLocation.File(0), reference, data);

        private static DictionaryToken Dictionary(params (string Key, IToken Value)[] entries) =>
            new DictionaryToken(entries.ToDictionary(static e => NameToken.Create(e.Key), static e => e.Value));
    }
}
