namespace UglyToad.PdfPig.Tests.Writer
{
    using Integration;
    using PdfPig.Core;
    using PdfPig.Parser.Parts;
    using PdfPig.Tokens;
    using PdfPig.Writer;
    using static RawPdf;

    public class PdfDocumentAssemblerTests
    {
        [Fact]
        public void PagesComeOutInTheOrderGiven_ThoseLeftOutGone()
        {
            using var source = new SampleDocument("Alpha", "Beta", "Gamma", "Delta").Open();

            using var result = Assemble(Page(source, 3), Page(source, 1), Page(source, 4));

            Assert.Equal(new[] { "Gamma", "Alpha", "Delta" }, Texts(result));
        }

        [Fact]
        public void PagesComeFromSeveralDocuments()
        {
            using var first = new SampleDocument("Alpha", "Beta").Open();
            using var second = new SampleDocument("Gamma").Open();

            using var result = Assemble(Page(second, 1), Page(first, 2), Page(first, 1));

            Assert.Equal(new[] { "Gamma", "Beta", "Alpha" }, Texts(result));
        }

        [Fact]
        public void APageAssembledTwice_IsRefused()
        {
            using var source = new SampleDocument("Alpha", "Beta").Open();

            Assert.Throws<ArgumentException>(() => Assemble(Page(source, 1), Page(source, 1)));
        }

        [Fact]
        public void ACancelledAssembly_Stops()
        {
            using var source = new SampleDocument("Alpha", "Beta").Open();
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            Assert.ThrowsAny<OperationCanceledException>(
                () => PdfDocumentAssembler.Assemble([Page(source, 1)], new MemoryStream(), cancellationToken: cancelled.Token));
        }

        [Fact]
        public void ADocumentWithAnXfaForm_IsRefused()
        {
            var document = new SampleDocument("Alpha") { CatalogEntries = "/AcroForm << /Fields [] /XFA (template) >>" };
            using var source = document.Open();

            var refused = Assert.Throws<AssemblyRefusedException>(() => Assemble(Page(source, 1)));

            Assert.Equal(AssemblyRefusal.XfaForm, refused.Reason);
        }

        [Fact]
        public void AnEncryptedDocument_IsWrittenPlain()
        {
            var path = IntegrationHelpers.GetSpecificTestDocumentPath("encrypted-password-is-password");
            using var source = PdfDocument.Open(path, new ParsingOptions { Password = "password" });
            Assert.True(source.IsEncrypted);

            using var result = Assemble(Page(source, 1));

            Assert.False(result.IsEncrypted);
            Assert.Equal(source.GetPage(1).Text, result.GetPage(1).Text);
        }

        [Fact]
        public void TheOutline_FollowsItsPages_AnItemWhosePageIsLeftOutStaysAHeadingOverItsItems()
        {
            var document = new SampleDocument("One", "Two", "Three", "Four");
            var pdf = document.Pdf;
            var outlines = pdf.Reserve();
            var one = pdf.Reserve();
            var oneA = pdf.Reserve();
            var three = pdf.Reserve();
            var four = pdf.Reserve();
            pdf.Set(outlines, $"<< /Type /Outlines /First {Ref(one)} /Last {Ref(four)} /Count 3 >>");
            pdf.Set(one, $"<< /Title (One) /Parent {Ref(outlines)} /Next {Ref(three)} /First {Ref(oneA)} /Last {Ref(oneA)} /Count -1 /C [1 0 0] /Dest [{Ref(document.PageRefs[0])} /Fit] >>");
            pdf.Set(oneA, $"<< /Title (OneA) /Parent {Ref(one)} /Dest [{Ref(document.PageRefs[1])} /Fit] >>");
            pdf.Set(three, $"<< /Title (Three) /Parent {Ref(outlines)} /Prev {Ref(one)} /Next {Ref(four)} /Dest [{Ref(document.PageRefs[2])} /Fit] >>");
            pdf.Set(four, $"<< /Title (Four) /Parent {Ref(outlines)} /Prev {Ref(three)} /Dest [{Ref(document.PageRefs[3])} /Fit] >>");
            document.CatalogEntries = $"/Outlines {Ref(outlines)}";
            using var source = document.Open();

            using var result = Assemble(Page(source, 4), Page(source, 2), Page(source, 3));

            var outline = Outline(result);
            Assert.Equal(
                [("Four", 0, (int?)0, (int?)null), ("One", 0, null, -1), ("OneA", 1, 1, null), ("Three", 0, 2, null)],
                outline.Select(item => (item.Title, item.Depth, item.Page, item.Count)).ToList());
            Assert.True(outline[1].Item.Data.ContainsKey("C"));
        }

        [Fact]
        public void AField_KeepsTheWidgetsOnThePagesKept_AndTheSameObjectsThePagesList()
        {
            var document = new SampleDocument("One", "Two");
            var pdf = document.Pdf;
            var parent = pdf.Reserve();
            var first = pdf.Reserve();
            var second = pdf.Reserve();
            var solo = pdf.Reserve();
            var hidden = pdf.Reserve();
            pdf.Set(first, $"<< /Type /Annot /Subtype /Widget /Rect [72 600 300 620] /Parent {Ref(parent)} /P {Ref(document.PageRefs[0])} >>");
            pdf.Set(second, $"<< /Type /Annot /Subtype /Widget /Rect [72 600 300 620] /Parent {Ref(parent)} /P {Ref(document.PageRefs[1])} >>");
            pdf.Set(parent, $"<< /FT /Tx /T (name) /Kids [{Ref(first)} {Ref(second)}] >>");
            pdf.Set(solo, $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (solo) /Rect [72 500 300 520] /P {Ref(document.PageRefs[1])} >>");
            pdf.Set(hidden, "<< /FT /Tx /T (hidden) /V (kept) >>");
            document.OnPage(0, $"/Annots [{Ref(first)}]").OnPage(1, $"/Annots [{Ref(second)} {Ref(solo)}]");
            document.CatalogEntries = $"/AcroForm << /Fields [{Ref(parent)} {Ref(solo)} {Ref(hidden)}] /DA (/Helv 0 Tf 0 g) /DR << /Font << /Helv {Ref(document.Font)} >> >> >>";
            using var source = document.Open();

            using var result = Assemble(Page(source, 1));

            var fields = Get<ArrayToken>(result, Form(result).Data["Fields"]);
            Assert.Equal(["name", "hidden"], fields.Data.Select(field => Text(result, Get<DictionaryToken>(result, field).Data["T"])).ToList());

            var kids = Get<ArrayToken>(result, Get<DictionaryToken>(result, fields.Data[0]).Data["Kids"]);
            var annotations = Get<ArrayToken>(result, PageDictionary(result, 1).Data["Annots"]);
            Assert.Equal(Reference(annotations.Data.Single()), Reference(kids.Data.Single()));
            Assert.Equal(Reference(fields.Data[0]), Reference(Get<DictionaryToken>(result, kids.Data[0]).Data["Parent"]));
        }

        [Fact]
        public void FormsFromTwoDocuments_RenameAClashingFieldAndDefaultFontApart()
        {
            var firstDocument = FormDocument("Alpha", "(/Helv 0 Tf 0 g)", "Helvetica");
            var secondDocument = FormDocument("Beta", "(/Helv 9 Tf 0 g)", "Courier");
            using var first = firstDocument.Open();
            using var second = secondDocument.Open();

            using var result = Assemble(Page(first, 1), Page(second, 1));

            var form = Form(result);
            var fields = Get<ArrayToken>(result, form.Data["Fields"]).Data.Select(field => Get<DictionaryToken>(result, field)).ToList();
            Assert.Equal(["name", "name_2"], fields.Select(field => Text(result, field.Data["T"])).ToList());
            Assert.Equal("/Helv_2 9 Tf 0 g", Text(result, fields[1].Data["DA"]));

            var fonts = Get<DictionaryToken>(result, Get<DictionaryToken>(result, form.Data["DR"]).Data["Font"]);
            Assert.Equal(["Helv", "Helv_2"], fonts.Data.Keys.OrderBy(key => key, StringComparer.Ordinal).ToList());
        }

        [Fact]
        public void APopupAndItsAnnotation_StayEachOthers_AndAReplyLetsGoOfAnAnnotationLeftOut()
        {
            var document = new SampleDocument("One", "Two");
            var pdf = document.Pdf;
            var note = pdf.Reserve();
            var popup = pdf.Reserve();
            var reply = pdf.Reserve();
            pdf.Set(note, $"<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] /Contents (note) /Popup {Ref(popup)} /P {Ref(document.PageRefs[0])} >>");
            pdf.Set(popup, $"<< /Type /Annot /Subtype /Popup /Rect [20 0 200 100] /Parent {Ref(note)} /P {Ref(document.PageRefs[0])} >>");
            pdf.Set(reply, $"<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] /Contents (reply) /IRT {Ref(note)} /P {Ref(document.PageRefs[1])} >>");
            document.OnPage(0, $"/Annots [{Ref(note)} {Ref(popup)}]").OnPage(1, $"/Annots [{Ref(reply)}]");
            using var source = document.Open();

            using (var result = Assemble(Page(source, 1)))
            {
                var annotations = Get<ArrayToken>(result, PageDictionary(result, 1).Data["Annots"]).Data;
                Assert.Equal(Reference(annotations[1]), Reference(Get<DictionaryToken>(result, annotations[0]).Data["Popup"]));
                Assert.Equal(Reference(annotations[0]), Reference(Get<DictionaryToken>(result, annotations[1]).Data["Parent"]));
            }

            using (var result = Assemble(Page(source, 2)))
            {
                var annotation = Get<DictionaryToken>(result, Get<ArrayToken>(result, PageDictionary(result, 1).Data["Annots"]).Data.Single());
                Assert.False(annotation.Data.ContainsKey("IRT"));
            }
        }

        [Fact]
        public void PageLabels_FollowTheirPages()
        {
            var document = new SampleDocument("i", "ii", "1", "2") { CatalogEntries = "/PageLabels << /Nums [0 << /S /r >> 2 << /S /D >>] >>" };
            using var source = document.Open();

            using (var reordered = Assemble(Page(source, 3), Page(source, 4), Page(source, 1)))
            {
                Assert.Equal([(0, "D", (int?)null), (2, "r", null)], Labels(reordered));
            }

            using (var deleted = Assemble(Page(source, 1), Page(source, 2), Page(source, 4)))
            {
                Assert.Equal([(0, "r", (int?)null), (2, "D", 2)], Labels(deleted));
            }
        }

        [Fact]
        public void ALinkToAPageLeftOut_GoesWithIt_AndNamedDestinationsToItAreLeftOut()
        {
            var document = new SampleDocument("One", "Two", "Three");
            var pdf = document.Pdf;
            var file = pdf.Add(Stream("/Type /EmbeddedFile", "hello"));
            var specification = pdf.Add($"<< /Type /Filespec /F (a.txt) /EF << /F {Ref(file)} >> >>");
            var toTwo = pdf.Add($"<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest [{Ref(document.PageRefs[1])} /Fit] >>");
            var toThree = pdf.Add($"<< /Type /Annot /Subtype /Link /Rect [0 10 10 20] /Dest [{Ref(document.PageRefs[2])} /Fit] >>");
            var namedThree = pdf.Add("<< /Type /Annot /Subtype /Link /Rect [0 20 10 30] /Dest (third) >>");
            var namedTwo = pdf.Add("<< /Type /Annot /Subtype /Link /Rect [0 30 10 40] /A << /S /GoTo /D (second) >> >>");
            document.OnPage(0, $"/Annots [{Ref(toTwo)} {Ref(toThree)} {Ref(namedThree)} {Ref(namedTwo)}]");
            document.CatalogEntries =
                $"/Names << /Dests << /Names [(second) [{Ref(document.PageRefs[1])} /Fit] (third) [{Ref(document.PageRefs[2])} /Fit]] >> /EmbeddedFiles << /Names [(a.txt) {Ref(specification)}] >> >>";
            using var source = document.Open();

            using var result = Assemble(Page(source, 1), Page(source, 2));

            var annotations = Get<ArrayToken>(result, PageDictionary(result, 1).Data["Annots"]).Data.Select(a => Get<DictionaryToken>(result, a)).ToList();
            Assert.Equal(2, annotations.Count);
            Assert.Equal(PageReferences(result)[1], Reference(Get<ArrayToken>(result, annotations[0].Data["Dest"]).Data[0]));
            Assert.Equal(["second"], NameTreeKeys(result, "Dests"));
            Assert.Equal(["a.txt"], NameTreeKeys(result, "EmbeddedFiles"));
        }

        [Fact]
        public void ALinkThroughANameOnlyItsOwnDocumentKnows_GoesStraightToItsPage()
        {
            using var first = new SampleDocument("Alpha").Open();
            var secondDocument = new SampleDocument("Beta", "Gamma");
            var link = secondDocument.Pdf.Add("<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest (gamma) >>");
            secondDocument.OnPage(0, $"/Annots [{Ref(link)}]");
            secondDocument.CatalogEntries = $"/Names << /Dests << /Names [(gamma) [{Ref(secondDocument.PageRefs[1])} /Fit]] >> >>";
            using var second = secondDocument.Open();

            using var result = Assemble(Page(first, 1), Page(second, 1), Page(second, 2));

            var annotation = Get<DictionaryToken>(result, Get<ArrayToken>(result, PageDictionary(result, 2).Data["Annots"]).Data.Single());
            Assert.Equal(PageReferences(result)[2], Reference(Get<ArrayToken>(result, annotation.Data["Dest"]).Data[0]));
        }

        [Fact]
        public void TheStructureTree_FollowsItsPages_AndEveryMarkedContentStillFindsItsElement()
        {
            using var source = TaggedDocument(["Alpha", "Beta", "Gamma"]).Open();

            using var result = Assemble(Page(source, 3), Page(source, 1));

            var documentElement = Get<DictionaryToken>(result, Get<ArrayToken>(result, StructureRoot(result).Data["K"]).Data.Single());
            var elements = Get<ArrayToken>(result, documentElement.Data["K"]).Data.Select(e => Get<DictionaryToken>(result, e)).ToList();
            var pages = PageReferences(result);
            Assert.Equal(pages, elements.Select(e => Reference(e.Data["Pg"])).ToList());

            var parentTree = ParentTree(result);
            for (var number = 1; number <= 2; number++)
            {
                var key = ((NumericToken)PageDictionary(result, number).Data["StructParents"]).Int;
                var owner = Get<DictionaryToken>(result, Get<ArrayToken>(result, parentTree[key]).Data[0]);
                Assert.Equal(pages[number - 1], Reference(owner.Data["Pg"]));
            }

            Assert.True(((BooleanToken)Get<DictionaryToken>(result, Catalog(result).Data["MarkInfo"]).Data["Marked"]).Data);
        }

        [Fact]
        public void TwoTaggedDocuments_BecomeOneTree_TheSecondsElementsPlacedByPage_AClashingRoleRenamedApart()
        {
            using var first = TaggedDocument(["Alpha", "Beta"], "Custom", "/RoleMap << /Custom /H1 >>").Open();
            using var second = TaggedDocument(["Gamma"], "Custom", "/RoleMap << /Custom /P >>").Open();

            using var result = Assemble(Page(first, 1), Page(second, 1), Page(first, 2));

            var firstDocument = Get<ArrayToken>(result, StructureRoot(result).Data["K"]).Data.Single();
            var kids = Get<ArrayToken>(result, Get<DictionaryToken>(result, firstDocument).Data["K"]).Data.Select(k => Get<DictionaryToken>(result, k)).ToList();
            Assert.Equal(["Custom", "Document", "Custom"], kids.Select(k => ((NameToken)k.Data["S"]).Data).ToList());
            Assert.Equal(Reference(firstDocument), Reference(kids[1].Data["P"]));

            var inserted = Get<DictionaryToken>(result, Get<ArrayToken>(result, kids[1].Data["K"]).Data.Single());
            Assert.Equal("Custom_2", ((NameToken)inserted.Data["S"]).Data);

            var roles = Get<DictionaryToken>(result, StructureRoot(result).Data["RoleMap"]);
            Assert.Equal("H1", ((NameToken)roles.Data["Custom"]).Data);
            Assert.Equal("P", ((NameToken)roles.Data["Custom_2"]).Data);

            var key = ((NumericToken)PageDictionary(result, 2).Data["StructParents"]).Int;
            Assert.Equal(2, key);
            var owner = Get<DictionaryToken>(result, Get<ArrayToken>(result, ParentTree(result)[key]).Data[0]);
            Assert.Equal(PageReferences(result)[1], Reference(owner.Data["Pg"]));
        }

        [Fact]
        public void AnUntaggedPageGivenATag_BecomesAFigureInTheTree()
        {
            using var tagged = TaggedDocument(["Alpha"]).Open();
            using var picture = new SampleDocument("Picture").Open();

            using var result = Assemble(Page(tagged, 1), Page(picture, 1, new AssembledContentTag("Figure", "A photo")));

            var page = PageDictionary(result, 2);
            Assert.Equal(3, Get<ArrayToken>(result, page.Data["Contents"]).Length);
            Assert.Equal("Picture", result.GetPage(2).Text);

            var figure = Get<DictionaryToken>(result, Get<ArrayToken>(result, ParentTree(result)[((NumericToken)page.Data["StructParents"]).Int]).Data[0]);
            Assert.Equal("Figure", ((NameToken)figure.Data["S"]).Data);
            Assert.Equal("A photo", Text(result, figure.Data["Alt"]));
            Assert.Equal(PageReferences(result)[1], Reference(figure.Data["Pg"]));

            var documentElement = Get<DictionaryToken>(result, Get<ArrayToken>(result, StructureRoot(result).Data["K"]).Data.Single());
            Assert.Equal(2, Get<ArrayToken>(result, documentElement.Data["K"]).Length);
        }

        [Fact]
        public void AnArticleThread_KeepsTheBeadsOnThePagesKept_ClosedIntoARing()
        {
            var document = new SampleDocument("One", "Two", "Three");
            var pdf = document.Pdf;
            var thread = pdf.Reserve();
            var beads = new[] { pdf.Reserve(), pdf.Reserve(), pdf.Reserve() };
            pdf.Set(thread, $"<< /Type /Thread /F {Ref(beads[0])} /I << /Title (Story) >> >>");
            for (var i = 0; i < 3; i++)
            {
                pdf.Set(beads[i], $"<< /Type /Bead /T {Ref(thread)} /N {Ref(beads[(i + 1) % 3])} /V {Ref(beads[(i + 2) % 3])} /P {Ref(document.PageRefs[i])} /R [0 0 100 100] >>");
                document.OnPage(i, $"/B [{Ref(beads[i])}]");
            }

            document.CatalogEntries = $"/Threads [{Ref(thread)}]";
            using var source = document.Open();

            using var result = Assemble(Page(source, 1), Page(source, 3));

            var threadReference = Get<ArrayToken>(result, Catalog(result).Data["Threads"]).Data.Single();
            var first = Reference(Get<ArrayToken>(result, PageDictionary(result, 1).Data["B"]).Data.Single());
            var last = Reference(Get<ArrayToken>(result, PageDictionary(result, 2).Data["B"]).Data.Single());
            Assert.Equal(first, Reference(Get<DictionaryToken>(result, threadReference).Data["F"]));

            var firstBead = Get<DictionaryToken>(result, new IndirectReferenceToken(first));
            var lastBead = Get<DictionaryToken>(result, new IndirectReferenceToken(last));
            Assert.Equal(last, Reference(firstBead.Data["N"]));
            Assert.Equal(last, Reference(firstBead.Data["V"]));
            Assert.Equal(first, Reference(lastBead.Data["N"]));
            Assert.Equal(Reference(threadReference), Reference(lastBead.Data["T"]));
        }

        [Fact]
        public void OptionalContentAndOutputIntents_FromEveryDocumentAreJoined()
        {
            var firstDocument = new SampleDocument("Alpha");
            var firstGroup = firstDocument.Pdf.Add("<< /Type /OCG /Name (One) >>");
            firstDocument.CatalogEntries = $"/OCProperties << /OCGs [{Ref(firstGroup)}] /D << /Order [{Ref(firstGroup)}] /OFF [{Ref(firstGroup)}] >> >> "
                                           + "/OutputIntents [<< /Type /OutputIntent /S /GTS_PDFA1 /OutputConditionIdentifier (sRGB) >>]";
            var secondDocument = new SampleDocument("Beta");
            var secondGroup = secondDocument.Pdf.Add("<< /Type /OCG /Name (Two) >>");
            secondDocument.CatalogEntries = $"/OCProperties << /OCGs [{Ref(secondGroup)}] /D << /Order [{Ref(secondGroup)}] /ON [{Ref(secondGroup)}] >> >>";
            using var first = firstDocument.Open();
            using var second = secondDocument.Open();

            using var result = Assemble(Page(first, 1), Page(second, 1));

            var properties = Get<DictionaryToken>(result, Catalog(result).Data["OCProperties"]);
            Assert.Equal(2, Get<ArrayToken>(result, properties.Data["OCGs"]).Length);
            var configuration = Get<DictionaryToken>(result, properties.Data["D"]);
            Assert.Equal(2, Get<ArrayToken>(result, configuration.Data["Order"]).Length);
            Assert.Single(Get<ArrayToken>(result, configuration.Data["OFF"]).Data);
            Assert.Single(Get<ArrayToken>(result, configuration.Data["ON"]).Data);
            Assert.Single(Get<ArrayToken>(result, Catalog(result).Data["OutputIntents"]).Data);
        }

        [Theory]
        [InlineData("AcroFormsBasicFields")]
        [InlineData("outline")]
        [InlineData("toc")]
        [InlineData("Single Page Hyperlinks - from open office")]
        [InlineData("Layer pdf - 322_High_Holborn_building_Brochure")]
        [InlineData("Pig Production Handbook")]
        public void AWholeDocumentReversed_KeepsEveryPage(string name)
        {
            using var source = PdfDocument.Open(IntegrationHelpers.GetDocumentPath(name));
            var pages = Enumerable.Range(1, source.NumberOfPages).Reverse().Select(number => Page(source, number)).ToArray();

            using var result = Assemble(pages);

            Assert.Equal(source.NumberOfPages, result.NumberOfPages);
            for (var i = 0; i < pages.Length; i++)
            {
                Assert.Equal(source.GetPage(pages[i].PageNumber).Text, result.GetPage(i + 1).Text);
            }

            if (source.TryGetForm(out var sourceForm))
            {
                Assert.True(result.TryGetForm(out var resultForm));
                Assert.Equal(sourceForm.Fields.Count, resultForm.Fields.Count);
            }
        }

        private static AssembledPage Page(PdfDocument document, int number, AssembledContentTag? tag = null) => new(document, number, tag);

        private static PdfDocument Assemble(params AssembledPage[] pages)
        {
            using var output = new MemoryStream();
            PdfDocumentAssembler.Assemble(pages, output);
            return PdfDocument.Open(output.ToArray(), ParsingOptions.LenientParsingOff);
        }

        private static SampleDocument FormDocument(string word, string appearance, string font)
        {
            var document = new SampleDocument(word);
            var pdf = document.Pdf;
            var formFont = pdf.Add($"<< /Type /Font /Subtype /Type1 /BaseFont /{font} >>");
            var field = pdf.Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [72 600 300 620] /P {Ref(document.PageRefs[0])} >>");
            document.OnPage(0, $"/Annots [{Ref(field)}]");
            document.CatalogEntries = $"/AcroForm << /Fields [{Ref(field)}] /DA {appearance} /DR << /Font << /Helv {Ref(formFont)} >> >> >>";
            return document;
        }

        /// <summary>
        /// A tagged document: a Document element over one element per page, each holding its page's word as marked
        /// content 0, keyed in the parent tree by the page's index.
        /// </summary>
        private static SampleDocument TaggedDocument(string[] words, string role = "P", string rootEntries = "")
        {
            var document = new SampleDocument(words);
            var pdf = document.Pdf;
            var root = pdf.Reserve();
            var documentElement = pdf.Reserve();
            var elements = words.Select(_ => pdf.Reserve()).ToArray();
            for (var i = 0; i < words.Length; i++)
            {
                pdf.Set(elements[i], $"<< /Type /StructElem /S /{role} /P {Ref(documentElement)} /Pg {Ref(document.PageRefs[i])} /K 0 >>");
                document.Tagged(i, role, 0).OnPage(i, $"/StructParents {i}");
            }

            pdf.Set(documentElement, $"<< /Type /StructElem /S /Document /P {Ref(root)} /K {Refs(elements)} >>");
            var numbers = string.Join(" ", elements.Select((element, i) => $"{i} [{Ref(element)}]"));
            pdf.Set(root, $"<< /Type /StructTreeRoot /K {Ref(documentElement)} /ParentTree << /Nums [{numbers}] >> /ParentTreeNextKey {words.Length} {rootEntries} >>");
            document.CatalogEntries = $"/StructTreeRoot {Ref(root)} /MarkInfo << /Marked true >> /Lang (en)";
            return document;
        }

        private static string[] Texts(PdfDocument document) => document.GetPages().Select(page => page.Text).ToArray();

        private static T Get<T>(PdfDocument document, IToken token) where T : class, IToken
            => DirectObjectFinder.Get<T>(token, document.Structure.TokenScanner)!;

        private static string Text(PdfDocument document, IToken token) => ((IDataToken<string>)Get<IToken>(document, token)).Data;

        private static IndirectReference Reference(IToken token) => ((IndirectReferenceToken)token).Data;

        private static DictionaryToken Catalog(PdfDocument document) => document.Structure.Catalog.CatalogDictionary;

        private static DictionaryToken Form(PdfDocument document) => Get<DictionaryToken>(document, Catalog(document).Data["AcroForm"]);

        private static DictionaryToken StructureRoot(PdfDocument document) => Get<DictionaryToken>(document, Catalog(document).Data["StructTreeRoot"]);

        private static IReadOnlyList<IndirectReference> PageReferences(PdfDocument document)
        {
            var result = new List<IndirectReference>();

            void Walk(IToken node)
            {
                var dictionary = Get<DictionaryToken>(document, node);
                if (((NameToken)dictionary.Data["Type"]).Data == "Page")
                {
                    result.Add(Reference(node));
                    return;
                }

                foreach (var kid in Get<ArrayToken>(document, dictionary.Data["Kids"]).Data)
                {
                    Walk(kid);
                }
            }

            Walk(Catalog(document).Data["Pages"]);
            return result;
        }

        private static DictionaryToken PageDictionary(PdfDocument document, int number)
            => Get<DictionaryToken>(document, new IndirectReferenceToken(PageReferences(document)[number - 1]));

        private static Dictionary<int, IToken> ParentTree(PdfDocument document)
        {
            var tree = Get<DictionaryToken>(document, StructureRoot(document).Data["ParentTree"]);
            return NumberTreeParser.Flatten(tree, document.Structure.TokenScanner).ToDictionary(entry => entry.Key, entry => entry.Value);
        }

        private static List<(int Index, string? Style, int? Start)> Labels(PdfDocument document)
        {
            var labels = Get<DictionaryToken>(document, Catalog(document).Data["PageLabels"]);
            return NumberTreeParser.Flatten(labels, document.Structure.TokenScanner)
                .Select(entry =>
                {
                    var range = Get<DictionaryToken>(document, entry.Value);
                    return (entry.Key,
                        range.TryGet(NameToken.S, out NameToken? style) ? style.Data : null,
                        range.TryGet(NameToken.St, out NumericToken? start) ? (int?)start.Int : null);
                })
                .ToList();
        }

        private static List<string> NameTreeKeys(PdfDocument document, string category)
        {
            var names = Get<DictionaryToken>(document, Catalog(document).Data["Names"]);
            var tree = Get<DictionaryToken>(document, names.Data[category]);
            var entries = Get<ArrayToken>(document, tree.Data["Names"]).Data;
            return entries.Where((_, i) => i % 2 == 0).Select(key => ((IDataToken<string>)key).Data).ToList();
        }

        private static List<(string Title, int Depth, int? Page, int? Count, DictionaryToken Item)> Outline(PdfDocument document)
        {
            var result = new List<(string, int, int?, int?, DictionaryToken)>();
            var pages = PageReferences(document);

            void Walk(DictionaryToken parent, int depth)
            {
                var token = parent.Data.TryGetValue("First", out var first) ? first : null;
                while (token is not null)
                {
                    var item = Get<DictionaryToken>(document, token);
                    int? page = item.Data.TryGetValue("Dest", out var destination)
                        ? pages.ToList().IndexOf(Reference(Get<ArrayToken>(document, destination).Data[0]))
                        : null;
                    int? count = item.Data.TryGetValue("Count", out var c) ? ((NumericToken)c).Int : null;
                    result.Add((Text(document, item.Data["Title"]), depth, page, count, item));
                    Walk(item, depth + 1);
                    token = item.Data.TryGetValue("Next", out var next) ? next : null;
                }
            }

            Walk(Get<DictionaryToken>(document, Catalog(document).Data["Outlines"]), 0);
            return result;
        }
    }
}
