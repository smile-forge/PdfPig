namespace UglyToad.PdfPig.Tests.Writer
{
    using System.Text;
    using PdfPig.Core;
    using PdfPig.Parser.Parts;
    using PdfPig.Tokens;
    using PdfPig.Writer;
    using static RawPdf;

    /// <summary>
    /// Pages placed on sheets of their own size — several printed to one sheet, one scaled to another size, one turned —
    /// each drawn from a form of its content, clipped to the part of it shown; and what lies on a page — its links and
    /// other annotations, form widgets, article beads, tagged content, and the destinations into it — moved with it.
    /// </summary>
    public class PdfDocumentAssemblerSheetTests
    {
        private const double Letter = 792;
        private const double Width = 612;

        private static readonly TransformationMatrix Identity = TransformationMatrix.Identity;

        [Fact]
        public void TwoPages_SideBySide_AreEachDrawnFromAFormOfTheirContent_InTheirCell()
        {
            using var source = new SampleDocument("Left", "Right").Open();

            using var result = Assemble(Sheet(2 * Width, Letter, Placed(source, 1, Identity), Placed(source, 2, Move(Width, 0))));

            Assert.Equal(1, result.NumberOfPages);
            var page = result.GetPage(1);
            Assert.Equal(2 * Width, page.Width);
            Assert.Equal("Left", Text(page.Letters.Where(letter => letter.StartBaseLine.X < Width)));
            Assert.Equal("Right", Text(page.Letters.Where(letter => letter.StartBaseLine.X >= Width)));
            Assert.Equal(72 + Width, page.Letters.First(letter => letter.Value == "R").StartBaseLine.X, 3);

            var forms = XObjects(result, 1);
            Assert.Equal(2, forms.Count);
            foreach (var form in forms.Values)
            {
                var dictionary = Get<StreamToken>(result, form).StreamDictionary;
                Assert.Equal("Form", ((NameToken)dictionary.Data["Subtype"]).Data);
                Assert.Equal(new double[] { 0, 0, Width, Letter }, Numbers(result, dictionary.Data["BBox"]));
            }
        }

        [Fact]
        public void APageScaled_IsDrawnAtItsNewSize_ItsTextWithIt()
        {
            using var source = new SampleDocument("Shrunk").Open();
            var scale = 1 / Math.Sqrt(2);

            using var result = Assemble(Sheet(Width * scale, Letter * scale, Placed(source, 1, TransformationMatrix.GetScaleMatrix(scale, scale))));

            var unscaled = source.GetPage(1).Letters.First();
            var first = result.GetPage(1).Letters.First();
            Assert.Equal(72 * scale, first.StartBaseLine.X, 3);
            Assert.Equal(700 * scale, first.StartBaseLine.Y, 3);
            Assert.Equal(unscaled.GlyphRectangle.Width * scale, first.GlyphRectangle.Width, 3);
            Assert.Equal(unscaled.GlyphRectangle.Height * scale, first.GlyphRectangle.Height, 3);
        }

        [Fact]
        public void ARotatedPage_StandsOnTheSheet_AsItIsShown()
        {
            using var source = new SampleDocument("Turned").OnPage(0, "/Rotate 90").Open();
            var shown = source.GetPage(1);

            using var result = Assemble(Sheet(Letter, Width, Placed(source, 1, Identity)));

            var placed = result.GetPage(1);
            Assert.Equal(Letter, placed.Width);
            Assert.Equal(Text(shown.Letters), Text(placed.Letters));
            Assert.Equal(shown.Letters[0].StartBaseLine.X, placed.Letters[0].StartBaseLine.X, 3);
            Assert.Equal(shown.Letters[0].StartBaseLine.Y, placed.Letters[0].StartBaseLine.Y, 3);
        }

        [Fact]
        public void APlacementTurnedAQuarterTurn_TurnsThePageWithIt()
        {
            using var source = new SampleDocument("Sideways").Open();

            using var result = Assemble(Sheet(Letter, Width, Placed(source, 1, QuarterTurn())));

            var first = result.GetPage(1).Letters.First();
            Assert.Equal(Letter - 700, first.StartBaseLine.X, 3);
            Assert.Equal(72, first.StartBaseLine.Y, 3);
        }

        [Fact]
        public void APlacementThatMirrorsOrSkews_IsRefused()
        {
            using var source = new SampleDocument("Page").Open();
            var page = new AssembledPage(source, 1);

            Assert.Throws<ArgumentException>(() => new AssembledPlacement(page, TransformationMatrix.FromValues(1, 0, 0, -1, 0, 0)));
            Assert.Throws<ArgumentException>(() => new AssembledPlacement(page, TransformationMatrix.FromValues(1, 0.5, 0, 1, 0, 0)));
            Assert.Throws<ArgumentException>(() => new AssembledPlacement(page, TransformationMatrix.FromValues(0, 0, 0, 0, 0, 0)));
        }

        [Fact]
        public void ThePartsOfOnePage_PlacedBackSideBySide_ShareItsContentAndResources()
        {
            using var source = new SampleDocument("Sheet")
                .Drawn(0, "BT /F1 24 Tf 72 700 Td (Top) Tj ET BT /F1 24 Tf 72 100 Td (Bottom) Tj ET")
                .Open();
            var top = new AssembledPage(source, 1, region: new PdfRectangle(0, 396, Width, Letter));
            var bottom = new AssembledPage(source, 1, region: new PdfRectangle(0, 0, Width, 396));

            using var result = Assemble(Sheet(2 * Width, 396, new AssembledPlacement(top, Identity), new AssembledPlacement(bottom, Move(Width, 0))));

            Assert.Single(XObjects(result, 1));
            var content = SheetContent(result, 1);
            Assert.Equal(2, CountOf(content, " Do "));
            // Each placement draws the whole page, clipped to its part: what the sheet shows is what lies on it.
            var shown = result.GetPage(1).Letters.Where(letter => letter.StartBaseLine.Y >= 0 && letter.StartBaseLine.Y <= 396).ToList();
            Assert.Equal(72, shown.First(letter => letter.Value == "T").StartBaseLine.X, 3);
            Assert.Equal(304, shown.First(letter => letter.Value == "T").StartBaseLine.Y, 3);
            Assert.Equal(72 + Width, shown.First(letter => letter.Value == "B").StartBaseLine.X, 3);
        }

        [Fact]
        public void APagePlacedTwice_SharesOneForm_ButEachPlacementShowsALinkOfItsOwn()
        {
            var document = new SampleDocument("Twice");
            var link = document.Pdf.Add($"<< /Type /Annot /Subtype /Link /Rect [72 690 150 720] /A << /S /URI /URI (https://twice.example) >> /P {Ref(document.PageRefs[0])} >>");
            document.OnPage(0, $"/Annots [{Ref(link)}]");
            using var source = document.Open();

            using var result = Assemble(Sheet(2 * Width, Letter, Placed(source, 1, Identity), Placed(source, 1, Move(Width, 0))));

            Assert.Single(XObjects(result, 1));
            var annotations = Get<ArrayToken>(result, PageDictionary(result, 1).Data["Annots"]).Data;
            Assert.Equal(2, annotations.Count);
            Assert.NotEqual(Reference(annotations[0]), Reference(annotations[1]));
            Assert.Equal(new double[] { 72, 690, 150, 720 }, Numbers(result, Get<DictionaryToken>(result, annotations[0]).Data["Rect"]));
            Assert.Equal(new double[] { 72 + Width, 690, 150 + Width, 720 }, Numbers(result, Get<DictionaryToken>(result, annotations[1]).Data["Rect"]));
        }

        [Fact]
        public void ALink_MovesAndScalesWithItsPage_AndSaysItIsOnTheSheet()
        {
            var document = new SampleDocument("Linked");
            var link = document.Pdf.Add(
                $"<< /Type /Annot /Subtype /Link /Rect [72 690 150 720] /QuadPoints [72 720 150 720 72 690 150 690] /A << /S /URI /URI (https://moved.example) >> /P {Ref(document.PageRefs[0])} >>");
            document.OnPage(0, $"/Annots [{Ref(link)}]");
            using var source = document.Open();

            using var result = Assemble(Sheet(Width, Letter, Placed(source, 1, TransformationMatrix.GetScaleMatrix(0.5, 0.5).Multiply(Move(100, 50)))));

            var annotation = Get<DictionaryToken>(result, Get<ArrayToken>(result, PageDictionary(result, 1).Data["Annots"]).Data.Single());
            Assert.Equal(new double[] { 136, 395, 175, 410 }, Numbers(result, annotation.Data["Rect"]));
            Assert.Equal(new double[] { 136, 410, 175, 410, 136, 395, 175, 395 }, Numbers(result, annotation.Data["QuadPoints"]));
            Assert.Equal(PageReferences(result)[0], Reference(annotation.Data["P"]));
        }

        [Fact]
        public void DestinationsIntoAPlacedPage_AreMovedWithIt()
        {
            var document = new SampleDocument("Target", "Links");
            var pdf = document.Pdf;
            var toPoint = pdf.Add($"<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest [{Ref(document.PageRefs[0])} /XYZ 0 700 0] >>");
            var toPage = pdf.Add($"<< /Type /Annot /Subtype /Link /Rect [0 20 10 30] /Dest [{Ref(document.PageRefs[0])} /Fit] >>");
            var toLine = pdf.Add($"<< /Type /Annot /Subtype /Link /Rect [0 40 10 50] /Dest [{Ref(document.PageRefs[0])} /FitH 100] >>");
            document.OnPage(1, $"/Annots [{Ref(toPoint)} {Ref(toPage)} {Ref(toLine)}]");
            using var source = document.Open();

            using (var moved = Assemble(
                       Sheet(2 * Width, Letter, Placed(source, 1, Move(Width, 0))),
                       AssembledSheet.Of(new AssembledPage(source, 2))))
            {
                var sheet = PageReferences(moved)[0];
                var links = Links(moved, 2);
                Assert.Equal(sheet, Reference(links[0].Data[0]));
                Assert.Equal("XYZ", ((NameToken)links[0].Data[1]).Data);
                Assert.Equal(new double[] { Width, 700 }, links[0].Data.Skip(2).Take(2).Select(n => ((NumericToken)n).Double));
                Assert.Equal("FitR", ((NameToken)links[1].Data[1]).Data);
                Assert.Equal(new double[] { Width, 0, 2 * Width, Letter }, links[1].Data.Skip(2).Select(n => ((NumericToken)n).Double));
                Assert.Equal("FitH", ((NameToken)links[2].Data[1]).Data);
                Assert.Equal(100, ((NumericToken)links[2].Data[2]).Double, 3);
            }

            using (var turned = Assemble(
                       Sheet(Letter, Width, Placed(source, 1, QuarterTurn())),
                       AssembledSheet.Of(new AssembledPage(source, 2))))
            {
                var line = Links(turned, 2)[2];
                Assert.Equal("FitV", ((NameToken)line.Data[1]).Data);
                Assert.Equal(Letter - 100, ((NumericToken)line.Data[2]).Double, 3);
            }
        }

        [Fact]
        public void AWidget_StaysInItsField_MovedWithItsPage()
        {
            var document = new SampleDocument("Form");
            var widget = document.Pdf.Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [72 600 200 620] /P {Ref(document.PageRefs[0])} >>");
            document.OnPage(0, $"/Annots [{Ref(widget)}]");
            document.CatalogEntries = $"/AcroForm << /Fields [{Ref(widget)}] >>";
            using var source = document.Open();

            using var result = Assemble(Sheet(Width + 100, Letter, Placed(source, 1, Move(100, 0))));

            var onPage = Get<ArrayToken>(result, PageDictionary(result, 1).Data["Annots"]).Data.Single();
            var field = Get<ArrayToken>(result, Get<DictionaryToken>(result, Catalog(result).Data["AcroForm"]).Data["Fields"]).Data.Single();
            Assert.Equal(Reference(field), Reference(onPage));
            Assert.Equal(new double[] { 172, 600, 300, 620 }, Numbers(result, Get<DictionaryToken>(result, onPage).Data["Rect"]));
        }

        [Fact]
        public void AnAnnotationTurnedWithItsPage_HasItsAppearanceTurned_UnlessItKeepsUpright()
        {
            var document = new SampleDocument("Turned");
            var pdf = document.Pdf;
            var look = pdf.Add(Stream("/Type /XObject /Subtype /Form /BBox [0 0 128 20]", "0 0 128 20 re f"));
            var widget = pdf.Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [72 600 200 620] /MK << /R 0 >> /AP << /N {Ref(look)} >> /P {Ref(document.PageRefs[0])} >>");
            var note = pdf.Add($"<< /Type /Annot /Subtype /Text /F 16 /Rect [72 690 92 710] /Contents (upright) /P {Ref(document.PageRefs[0])} >>");
            document.OnPage(0, $"/Annots [{Ref(widget)} {Ref(note)}]");
            document.CatalogEntries = $"/AcroForm << /Fields [{Ref(widget)}] >>";
            using var source = document.Open();

            using var result = Assemble(Sheet(Letter, Width, Placed(source, 1, QuarterTurn())));

            var annotations = Get<ArrayToken>(result, PageDictionary(result, 1).Data["Annots"]).Data.Select(annotation => Get<DictionaryToken>(result, annotation)).ToList();
            var turned = annotations[0];
            Assert.Equal(new double[] { 172, 72, 192, 200 }, Numbers(result, turned.Data["Rect"]));
            Assert.Equal(90, Get<NumericToken>(result, Get<DictionaryToken>(result, turned.Data["MK"]).Data["R"]).Int);
            var appearance = Get<StreamToken>(result, Get<DictionaryToken>(result, turned.Data["AP"]).Data["N"]).StreamDictionary;
            Assert.Equal(new double[] { 0, 1, -1, 0, 0, 0 }, Numbers(result, appearance.Data["Matrix"]));

            // A note that keeps upright stays its own size, pinned where its top left corner now is.
            Assert.Equal(new double[] { 82, 52, 102, 72 }, Numbers(result, annotations[1].Data["Rect"]));
        }

        [Fact]
        public void AnArticleBead_MovesWithItsPage()
        {
            var document = new SampleDocument("Article");
            var pdf = document.Pdf;
            var thread = pdf.Reserve();
            var bead = pdf.Reserve();
            pdf.Set(thread, $"<< /Type /Thread /F {Ref(bead)} >>");
            pdf.Set(bead, $"<< /Type /Bead /T {Ref(thread)} /N {Ref(bead)} /V {Ref(bead)} /P {Ref(document.PageRefs[0])} /R [50 600 560 780] >>");
            document.OnPage(0, $"/B [{Ref(bead)}]");
            document.CatalogEntries = $"/Threads [{Ref(thread)}]";
            using var source = document.Open();

            using var result = Assemble(Sheet(Width, Letter + 100, Placed(source, 1, Move(0, 100))));

            var placed = Get<DictionaryToken>(result, Get<ArrayToken>(result, PageDictionary(result, 1).Data["B"]).Data.Single());
            Assert.Equal(new double[] { 50, 700, 560, 880 }, Numbers(result, placed.Data["R"]));
            Assert.Equal(PageReferences(result)[0], Reference(placed.Data["P"]));
        }

        [Fact]
        public void TaggedContent_OnAPlacedPage_IsNamedThroughTheFormItIsDrawnFrom()
        {
            using var source = TaggedSheet().Open();

            using var result = Assemble(Sheet(Width, Letter, Placed(source, 1, Identity)));

            var sheet = PageReferences(result)[0];
            var form = XObjects(result, 1).Values.Single();
            var structure = Get<DictionaryToken>(result, Catalog(result).Data["StructTreeRoot"]);
            var documentOut = Get<DictionaryToken>(result, Get<ArrayToken>(result, structure.Data["K"]).Data.Single());
            var element = Get<DictionaryToken>(result, Get<ArrayToken>(result, documentOut.Data["K"]).Data[0]);
            var marked = Get<DictionaryToken>(result, Get<ArrayToken>(result, element.Data["K"]).Data.Single());

            Assert.Equal("MCR", ((NameToken)marked.Data["Type"]).Data);
            Assert.Equal(sheet, Reference(marked.Data["Pg"]));
            Assert.Equal(Reference(form), Reference(marked.Data["Stm"]));
            Assert.Equal(0, ((NumericToken)marked.Data["MCID"]).Int);

            var key = ((NumericToken)Get<StreamToken>(result, form).StreamDictionary.Data["StructParents"]).Int;
            Assert.Contains(key, ParentTree(result, structure).Keys);
            Assert.False(PageDictionary(result, 1).Data.ContainsKey("StructParents"));
        }

        [Fact]
        public void AnUntaggedPageGivenATag_PlacedOnASheet_IsAFigureDrawnFromItsForm()
        {
            using var tagged = TaggedSheet().Open();
            using var plain = new SampleDocument("Picture").Open();

            using var result = Assemble(
                AssembledSheet.Of(new AssembledPage(tagged, 1)),
                Sheet(Width, Letter, new AssembledPlacement(new AssembledPage(plain, 1, new AssembledContentTag("Figure", "A picture")), Identity)));

            var form = XObjects(result, 2).Values.Single();
            var formStream = Get<StreamToken>(result, form);
            Assert.Contains("/Figure <</MCID 0>> BDC", Decoded(result, formStream));

            var structure = Get<DictionaryToken>(result, Catalog(result).Data["StructTreeRoot"]);
            var documentOut = Get<DictionaryToken>(result, Get<ArrayToken>(result, structure.Data["K"]).Data.Single());
            var figure = Get<ArrayToken>(result, documentOut.Data["K"]).Data
                .Select(kid => Get<DictionaryToken>(result, kid))
                .Single(element => ((NameToken)element.Data["S"]).Data == "Figure");
            var marked = (DictionaryToken)figure.Data["K"];
            Assert.Equal(Reference(form), Reference(marked.Data["Stm"]));
            Assert.Equal(PageReferences(result)[1], Reference(marked.Data["Pg"]));

            var key = ((NumericToken)formStream.StreamDictionary.Data["StructParents"]).Int;
            Assert.Contains(key, ParentTree(result, structure).Keys);
        }

        [Fact]
        public void ATransparencyGroup_GoesWithItsForm_AndTheSheet()
        {
            using var source = new SampleDocument("Glass").OnPage(0, "/Group << /S /Transparency /CS /DeviceRGB >>").Open();

            using var result = Assemble(Sheet(Width, Letter, Placed(source, 1, Identity)));

            var form = Get<StreamToken>(result, XObjects(result, 1).Values.Single()).StreamDictionary;
            Assert.True(form.ContainsKey(NameToken.Group));
            Assert.True(PageDictionary(result, 1).ContainsKey(NameToken.Group));
        }

        [Fact]
        public void SeveralContentStreams_AreJoinedIntoTheOneAFormHolds()
        {
            var pdf = new RawPdf();
            var catalog = pdf.Reserve();
            var pages = pdf.Reserve();
            var font = pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
            var first = pdf.Add(Stream(string.Empty, "BT /F1 24 Tf 72 700 Td (One) Tj ET"));
            var second = pdf.Add(Stream(string.Empty, "BT /F1 24 Tf 72 600 Td (Two) Tj ET"));
            var page = pdf.Add($"<< /Type /Page /Parent {Ref(pages)} /MediaBox [0 0 612 792] /Resources << /Font << /F1 {Ref(font)} >> >> /Contents [{Ref(first)} {Ref(second)}] >>");
            pdf.Set(pages, $"<< /Type /Pages /Kids [{Ref(page)}] /Count 1 >>");
            pdf.Set(catalog, $"<< /Type /Catalog /Pages {Ref(pages)} >>");
            using var source = PdfDocument.Open(pdf.Build(catalog), ParsingOptions.LenientParsingOff);

            using var result = Assemble(Sheet(Width, Letter, Placed(source, 1, Identity)));

            Assert.Equal("OneTwo", Text(result.GetPage(1).Letters));
        }

        [Fact]
        public void ASheetWithNothingPlacedOnIt_IsABlankPageOfItsSize()
        {
            using var source = new SampleDocument("Primary").Open();
            using var output = new MemoryStream();

            PdfDocumentAssembler.Assemble([new AssembledSheet(300, 400, [])], output, new AssemblyOptions { Primary = source });

            using var result = PdfDocument.Open(output.ToArray(), ParsingOptions.LenientParsingOff);
            Assert.Equal(1, result.NumberOfPages);
            Assert.Equal(300, result.GetPage(1).Width);
            Assert.Equal(400, result.GetPage(1).Height);
            Assert.Empty(result.GetPage(1).Letters);
        }

        [Fact]
        public void ASheet_IsLabelledByWhereItIs_APageWrittenAsItselfKeepingItsLabel()
        {
            var document = new SampleDocument("i", "ii") { CatalogEntries = "/PageLabels << /Nums [0 << /S /r >>] >>" };
            using var source = document.Open();

            using var result = Assemble(AssembledSheet.Of(new AssembledPage(source, 1)), Sheet(Width, Letter, Placed(source, 2, Identity)));

            var numbers = Get<ArrayToken>(result, Get<DictionaryToken>(result, Catalog(result).Data["PageLabels"]).Data["Nums"]).Data;
            Assert.Equal(4, numbers.Count);
            Assert.Equal("r", ((NameToken)Get<DictionaryToken>(result, numbers[1]).Data["S"]).Data);
            var sheetLabel = Get<DictionaryToken>(result, numbers[3]);
            Assert.Equal("D", ((NameToken)sheetLabel.Data["S"]).Data);
            Assert.Equal(2, ((NumericToken)sheetLabel.Data["St"]).Int);
        }

        [Fact]
        public void APageWrittenAsItself_IsWrittenExactlyAsAnAssemblyOfPagesWritesIt()
        {
            using var source = TaggedSheet().Open();
            var options = new AssemblyOptions { Information = new PdfDocumentBuilder.DocumentInformationBuilder { Title = "Same" } };
            var page = new AssembledPage(source, 1);

            using var fromPages = new MemoryStream();
            PdfDocumentAssembler.Assemble([page], fromPages, options);
            using var fromSheets = new MemoryStream();
            PdfDocumentAssembler.Assemble([AssembledSheet.Of(page)], fromSheets, options);

            // Every document written is given an identifier of its own, which is all that may differ.
            Assert.Equal(WithoutIdentifier(fromPages), WithoutIdentifier(fromSheets));
        }

        private static SampleDocument TaggedSheet()
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
            return document;
        }

        /// <summary>A page turned a quarter turn counterclockwise, onto a sheet as wide as the page is tall.</summary>
        private static TransformationMatrix QuarterTurn()
            => TransformationMatrix.FromValues(0, 1, -1, 0, 0, 0).Multiply(Move(Letter, 0));

        private static TransformationMatrix Move(double x, double y) => TransformationMatrix.GetTranslationMatrix(x, y);

        private static AssembledPlacement Placed(PdfDocument document, int number, TransformationMatrix place)
            => new(new AssembledPage(document, number), place);

        private static AssembledSheet Sheet(double width, double height, params AssembledPlacement[] placements) => new(width, height, placements);

        private static PdfDocument Assemble(params AssembledSheet[] sheets)
        {
            using var output = new MemoryStream();
            PdfDocumentAssembler.Assemble(sheets, output);
            return PdfDocument.Open(output.ToArray(), ParsingOptions.LenientParsingOff);
        }

        private static string Text(IEnumerable<PdfPig.Content.Letter> letters) => string.Concat(letters.Select(letter => letter.Value));

        private static int CountOf(string text, string part)
        {
            var count = 0;
            for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        private static string WithoutIdentifier(MemoryStream written)
            => System.Text.RegularExpressions.Regex.Replace(Encoding.ASCII.GetString(written.ToArray()), @"/ID \[[^\]]*\]", "/ID []");

        private static string SheetContent(PdfDocument document, int number)
            => Decoded(document, Get<StreamToken>(document, PageDictionary(document, number).Data["Contents"]));

        private static string Decoded(PdfDocument document, StreamToken stream)
            => Encoding.ASCII.GetString(stream.Decode(document.Structure.FilterProvider, document.Structure.TokenScanner).ToArray());

        private static IReadOnlyDictionary<string, IToken> XObjects(PdfDocument document, int number)
            => Get<DictionaryToken>(document, Get<DictionaryToken>(document, PageDictionary(document, number).Data["Resources"]).Data["XObject"]).Data;

        private static IReadOnlyList<ArrayToken> Links(PdfDocument document, int number)
            => Get<ArrayToken>(document, PageDictionary(document, number).Data["Annots"]).Data
                .Select(link => Get<ArrayToken>(document, Get<DictionaryToken>(document, link).Data["Dest"]))
                .ToList();

        private static IReadOnlyDictionary<int, IToken> ParentTree(PdfDocument document, DictionaryToken structure)
            => NumberTreeParser.Flatten(Get<DictionaryToken>(document, structure.Data["ParentTree"]), document.Structure.TokenScanner)
                .ToDictionary(entry => entry.Key, entry => entry.Value);

        private static double[] Numbers(PdfDocument document, IToken token)
            => Get<ArrayToken>(document, token).Data.Select(item => Math.Round(Get<NumericToken>(document, item).Double, 6)).ToArray();

        private static T Get<T>(PdfDocument document, IToken token) where T : class, IToken
            => DirectObjectFinder.Get<T>(token, document.Structure.TokenScanner)!;

        private static IndirectReference Reference(IToken token) => ((IndirectReferenceToken)token).Data;

        private static DictionaryToken Catalog(PdfDocument document) => document.Structure.Catalog.CatalogDictionary;

        private static IReadOnlyList<IndirectReference> PageReferences(PdfDocument document)
            => Get<ArrayToken>(document, Get<DictionaryToken>(document, Catalog(document).Data["Pages"]).Data["Kids"]).Data.Select(Reference).ToList();

        private static DictionaryToken PageDictionary(PdfDocument document, int number)
            => Get<DictionaryToken>(document, new IndirectReferenceToken(PageReferences(document)[number - 1]));
    }
}
