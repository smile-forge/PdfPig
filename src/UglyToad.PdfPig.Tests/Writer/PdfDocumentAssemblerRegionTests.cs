namespace UglyToad.PdfPig.Tests.Writer
{
    using PdfPig.Core;
    using PdfPig.Parser.Parts;
    using PdfPig.Tokens;
    using PdfPig.Writer;
    using static RawPdf;

    /// <summary>
    /// A page printed several to a sheet, assembled one part at a time: each part a page of its own showing only its
    /// region, and what lies on the sheet — annotations, links to it, tagged content, article beads — going to the part
    /// it lies in.
    /// </summary>
    public class PdfDocumentAssemblerRegionTests
    {
        private const string TwoUp = "BT /F1 24 Tf 72 700 Td (Top) Tj ET BT /F1 24 Tf 72 100 Td (Bottom) Tj ET";

        private static readonly PdfRectangle Top = new(0, 396, 612, 792);
        private static readonly PdfRectangle Bottom = new(0, 0, 612, 396);

        [Fact]
        public void EachPart_IsAPageShowingOnlyItsRegion()
        {
            using var source = new SampleDocument("Sheet").Drawn(0, TwoUp).Open();

            using var result = Assemble(Part(source, 1, Top), Part(source, 1, Bottom));

            Assert.Equal(2, result.NumberOfPages);
            Assert.Equal(Top, result.GetPage(1).CropBox.Bounds);
            Assert.Equal(Bottom, result.GetPage(2).CropBox.Bounds);
            Assert.Equal(396, result.GetPage(2).Height);
        }

        [Fact]
        public void AWholePageAndItsPart_MayBothBeAssembled_ButNotOnePartTwice()
        {
            using var source = new SampleDocument("Sheet").Drawn(0, TwoUp).Open();

            using (var result = Assemble(new AssembledPage(source, 1), Part(source, 1, Top)))
            {
                Assert.Equal(2, result.NumberOfPages);
            }

            Assert.Throws<ArgumentException>(() => Assemble(Part(source, 1, Top), Part(source, 1, Top)));
        }

        [Fact]
        public void AnAnnotation_GoesToThePartItLiesIn_AndSaysItIsOnThatPart()
        {
            var document = new SampleDocument("Sheet").Drawn(0, TwoUp);
            var pdf = document.Pdf;
            var top = pdf.Add($"<< /Type /Annot /Subtype /Link /Rect [72 690 150 720] /A << /S /URI /URI (https://top.example) >> /P {Ref(document.PageRefs[0])} >>");
            var bottom = pdf.Add($"<< /Type /Annot /Subtype /Link /Rect [72 90 150 120] /A << /S /URI /URI (https://bottom.example) >> /P {Ref(document.PageRefs[0])} >>");
            document.OnPage(0, $"/Annots [{Ref(top)} {Ref(bottom)}]");
            using var source = document.Open();

            using var result = Assemble(Part(source, 1, Top), Part(source, 1, Bottom));

            var pages = PageReferences(result);
            var first = Get<ArrayToken>(result, PageDictionary(result, 1).Data["Annots"]).Data.Single();
            var second = Get<ArrayToken>(result, PageDictionary(result, 2).Data["Annots"]).Data.Single();
            Assert.Equal("https://top.example", Uri(result, first));
            Assert.Equal("https://bottom.example", Uri(result, second));
            Assert.Equal(pages[1], Reference(Get<DictionaryToken>(result, second).Data["P"]));
        }

        [Fact]
        public void ALinkIntoASplitPage_GoesToThePartItsPointLiesIn()
        {
            var document = new SampleDocument("Sheet", "Links").Drawn(0, TwoUp);
            var pdf = document.Pdf;
            var toTop = pdf.Add($"<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest [{Ref(document.PageRefs[0])} /XYZ 0 700 0] >>");
            var toBottom = pdf.Add($"<< /Type /Annot /Subtype /Link /Rect [0 20 10 30] /Dest [{Ref(document.PageRefs[0])} /XYZ 0 100 0] >>");
            document.OnPage(1, $"/Annots [{Ref(toTop)} {Ref(toBottom)}]");
            using var source = document.Open();

            using var result = Assemble(Part(source, 1, Top), Part(source, 1, Bottom), new AssembledPage(source, 2));

            var pages = PageReferences(result);
            var links = Get<ArrayToken>(result, PageDictionary(result, 3).Data["Annots"]).Data.Select(link => Get<DictionaryToken>(result, link)).ToList();
            Assert.Equal(pages[0], Reference(Get<ArrayToken>(result, links[0].Data["Dest"]).Data[0]));
            Assert.Equal(pages[1], Reference(Get<ArrayToken>(result, links[1].Data["Dest"]).Data[0]));
        }

        [Fact]
        public void AnOutlineItemIntoASplitPage_GoesToThePartItsPointLiesIn()
        {
            var document = new SampleDocument("Sheet").Drawn(0, TwoUp);
            var pdf = document.Pdf;
            var outlines = pdf.Reserve();
            var top = pdf.Reserve();
            var bottom = pdf.Reserve();
            pdf.Set(outlines, $"<< /Type /Outlines /First {Ref(top)} /Last {Ref(bottom)} /Count 2 >>");
            pdf.Set(top, $"<< /Title (Top) /Parent {Ref(outlines)} /Next {Ref(bottom)} /Dest [{Ref(document.PageRefs[0])} /XYZ 0 700 0] >>");
            pdf.Set(bottom, $"<< /Title (Bottom) /Parent {Ref(outlines)} /Prev {Ref(top)} /Dest [{Ref(document.PageRefs[0])} /FitH 100] >>");
            document.CatalogEntries = $"/Outlines {Ref(outlines)}";
            using var source = document.Open();

            using var result = Assemble(Part(source, 1, Top), Part(source, 1, Bottom));

            var pages = PageReferences(result);
            var outline = Get<DictionaryToken>(result, Catalog(result).Data["Outlines"]);
            var first = Get<DictionaryToken>(result, outline.Data["First"]);
            var last = Get<DictionaryToken>(result, outline.Data["Last"]);
            Assert.Equal(pages[0], Reference(Get<ArrayToken>(result, first.Data["Dest"]).Data[0]));
            Assert.Equal(pages[1], Reference(Get<ArrayToken>(result, last.Data["Dest"]).Data[0]));
        }

        [Fact]
        public void TaggedContent_OnALaterPart_IsNamedWithThatPart_AndThePartHasAParentTreeKeyOfItsOwn()
        {
            var document = new SampleDocument("Sheet")
                .Drawn(0, "/P <</MCID 0>> BDC BT /F1 24 Tf 72 700 Td (Top) Tj ET EMC /P <</MCID 1>> BDC BT /F1 24 Tf 72 100 Td (Bottom) Tj ET EMC")
                .OnPage(0, "/StructParents 0");
            var pdf = document.Pdf;
            var root = pdf.Reserve();
            var documentElement = pdf.Reserve();
            var first = pdf.Reserve();
            var second = pdf.Reserve();
            pdf.Set(first, $"<< /Type /StructElem /S /P /P {Ref(documentElement)} /Pg {Ref(document.PageRefs[0])} /K 0 >>");
            pdf.Set(second, $"<< /Type /StructElem /S /P /P {Ref(documentElement)} /Pg {Ref(document.PageRefs[0])} /K 1 >>");
            pdf.Set(documentElement, $"<< /Type /StructElem /S /Document /P {Ref(root)} /K [{Ref(first)} {Ref(second)}] >>");
            pdf.Set(root, $"<< /Type /StructTreeRoot /K {Ref(documentElement)} /ParentTree << /Nums [0 [{Ref(first)} {Ref(second)}]] >> /ParentTreeNextKey 1 >>");
            document.CatalogEntries = $"/StructTreeRoot {Ref(root)} /MarkInfo << /Marked true >>";
            using var source = document.Open();

            using var result = Assemble(Part(source, 1, Top), Part(source, 1, Bottom));

            var pages = PageReferences(result);
            var structure = Get<DictionaryToken>(result, Catalog(result).Data["StructTreeRoot"]);
            var documentOut = Get<DictionaryToken>(result, Get<ArrayToken>(result, structure.Data["K"]).Data.Single());
            var elements = Get<ArrayToken>(result, documentOut.Data["K"]).Data.Select(element => Get<DictionaryToken>(result, element)).ToList();

            Assert.Equal(pages[0], Reference(elements[0].Data["Pg"]));
            var marked = Get<DictionaryToken>(result, Get<ArrayToken>(result, elements[1].Data["K"]).Data.Single());
            Assert.Equal(pages[1], Reference(marked.Data["Pg"]));
            Assert.Equal(1, ((NumericToken)marked.Data["MCID"]).Int);

            var firstKey = ((NumericToken)PageDictionary(result, 1).Data["StructParents"]).Int;
            var secondKey = ((NumericToken)PageDictionary(result, 2).Data["StructParents"]).Int;
            Assert.NotEqual(firstKey, secondKey);
            var parentTree = NumberTreeParser.Flatten(Get<DictionaryToken>(result, structure.Data["ParentTree"]), result.Structure.TokenScanner)
                .ToDictionary(entry => entry.Key, entry => entry.Value);
            Assert.True(parentTree.ContainsKey(firstKey) && parentTree.ContainsKey(secondKey));
        }

        [Fact]
        public void AnArticleBead_GoesToThePartItLiesIn()
        {
            var document = new SampleDocument("Sheet").Drawn(0, TwoUp);
            var pdf = document.Pdf;
            var thread = pdf.Reserve();
            var top = pdf.Reserve();
            var bottom = pdf.Reserve();
            pdf.Set(thread, $"<< /Type /Thread /F {Ref(top)} >>");
            pdf.Set(top, $"<< /Type /Bead /T {Ref(thread)} /N {Ref(bottom)} /V {Ref(bottom)} /P {Ref(document.PageRefs[0])} /R [50 600 560 780] >>");
            pdf.Set(bottom, $"<< /Type /Bead /T {Ref(thread)} /N {Ref(top)} /V {Ref(top)} /P {Ref(document.PageRefs[0])} /R [50 20 560 380] >>");
            document.OnPage(0, $"/B [{Ref(top)} {Ref(bottom)}]");
            document.CatalogEntries = $"/Threads [{Ref(thread)}]";
            using var source = document.Open();

            using var result = Assemble(Part(source, 1, Top), Part(source, 1, Bottom));

            var pages = PageReferences(result);
            var onFirst = Get<ArrayToken>(result, PageDictionary(result, 1).Data["B"]).Data.Single();
            var onSecond = Get<ArrayToken>(result, PageDictionary(result, 2).Data["B"]).Data.Single();
            Assert.Equal(pages[0], Reference(Get<DictionaryToken>(result, onFirst).Data["P"]));
            Assert.Equal(pages[1], Reference(Get<DictionaryToken>(result, onSecond).Data["P"]));
        }

        private static AssembledPage Part(PdfDocument document, int number, PdfRectangle region) => new(document, number, region: region);

        private static PdfDocument Assemble(params AssembledPage[] pages)
        {
            using var output = new MemoryStream();
            PdfDocumentAssembler.Assemble(pages, output);
            return PdfDocument.Open(output.ToArray(), ParsingOptions.LenientParsingOff);
        }

        private static T Get<T>(PdfDocument document, IToken token) where T : class, IToken
            => DirectObjectFinder.Get<T>(token, document.Structure.TokenScanner)!;

        private static string Uri(PdfDocument document, IToken annotation)
            => ((IDataToken<string>)Get<IToken>(document, Get<DictionaryToken>(document, Get<DictionaryToken>(document, annotation).Data["A"]).Data["URI"])).Data;

        private static IndirectReference Reference(IToken token) => ((IndirectReferenceToken)token).Data;

        private static DictionaryToken Catalog(PdfDocument document) => document.Structure.Catalog.CatalogDictionary;

        private static IReadOnlyList<IndirectReference> PageReferences(PdfDocument document)
            => Get<ArrayToken>(document, Get<DictionaryToken>(document, Catalog(document).Data["Pages"]).Data["Kids"]).Data.Select(Reference).ToList();

        private static DictionaryToken PageDictionary(PdfDocument document, int number)
            => Get<DictionaryToken>(document, new IndirectReferenceToken(PageReferences(document)[number - 1]));
    }
}
