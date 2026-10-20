namespace UglyToad.PdfPig.Tests.Writer
{
    using System.Globalization;
    using System.Text;

    /// <summary>
    /// Writes a PDF object by object, so a test can build a document whose every object it names — a tagged page, a
    /// field with its widgets, an article thread — and assert on exactly those objects once assembled.
    /// </summary>
    internal sealed class RawPdf
    {
        private readonly List<string?> bodies = new();

        public int Reserve()
        {
            bodies.Add(null);
            return bodies.Count;
        }

        public int Add(string body)
        {
            bodies.Add(body);
            return bodies.Count;
        }

        public void Set(int number, string body) => bodies[number - 1] = body;

        public static string Stream(string dictionary, string content)
            => $"<< {dictionary} /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream";

        public static string Ref(int number) => number + " 0 R";

        public static string Refs(IEnumerable<int> numbers) => "[" + string.Join(" ", numbers.Select(Ref)) + "]";

        public byte[] Build(int root, string trailer = "")
        {
            using var output = new MemoryStream();
            var offsets = new List<long>();

            void Write(string text)
            {
                var bytes = Encoding.ASCII.GetBytes(text);
                output.Write(bytes, 0, bytes.Length);
            }

            Write("%PDF-1.7\n");
            for (var i = 0; i < bodies.Count; i++)
            {
                offsets.Add(output.Position);
                Write($"{i + 1} 0 obj\n{bodies[i] ?? "null"}\nendobj\n");
            }

            var xref = output.Position;
            Write($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets)
            {
                Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
            }

            Write($"trailer\n<< /Size {bodies.Count + 1} /Root {Ref(root)} {trailer}>>\nstartxref\n{xref}\n%%EOF\n");
            return output.ToArray();
        }
    }

    /// <summary>
    /// A document of one-word pages under a single page tree node, to which a test adds what it is about.
    /// </summary>
    internal sealed class SampleDocument
    {
        private readonly string[] words;
        private readonly string[] pageEntries;
        private readonly string?[] contents;

        public SampleDocument(params string[] words)
        {
            this.words = words;
            Pdf = new RawPdf();
            Catalog = Pdf.Reserve();
            Pages = Pdf.Reserve();
            Font = Pdf.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
            PageRefs = words.Select(_ => Pdf.Reserve()).ToArray();
            pageEntries = words.Select(_ => string.Empty).ToArray();
            contents = new string?[words.Length];
        }

        public RawPdf Pdf { get; }

        public int Catalog { get; }

        public int Pages { get; }

        public int Font { get; }

        public int[] PageRefs { get; }

        public string CatalogEntries { get; set; } = string.Empty;

        /// <summary>
        /// Adds entries to page <paramref name="index"/> (from 0).
        /// </summary>
        public SampleDocument OnPage(int index, string entries)
        {
            pageEntries[index] += " " + entries;
            return this;
        }

        /// <summary>
        /// Wraps page <paramref name="index"/>'s word in a marked content sequence of <paramref name="tag"/>.
        /// </summary>
        public SampleDocument Tagged(int index, string tag, int mcid)
        {
            contents[index] = $"/{tag} <</MCID {mcid}>> BDC BT /F1 24 Tf 72 700 Td ({words[index]}) Tj ET EMC";
            return this;
        }

        public byte[] Build()
        {
            for (var i = 0; i < words.Length; i++)
            {
                var content = Pdf.Add(RawPdf.Stream(string.Empty, contents[i] ?? $"BT /F1 24 Tf 72 700 Td ({words[i]}) Tj ET"));
                Pdf.Set(
                    PageRefs[i],
                    $"<< /Type /Page /Parent {RawPdf.Ref(Pages)} /MediaBox [0 0 612 792] /Resources << /Font << /F1 {RawPdf.Ref(Font)} >> >> /Contents {RawPdf.Ref(content)}{pageEntries[i]} >>");
            }

            Pdf.Set(Pages, $"<< /Type /Pages /Kids {RawPdf.Refs(PageRefs)} /Count {PageRefs.Length} >>");
            Pdf.Set(Catalog, $"<< /Type /Catalog /Pages {RawPdf.Ref(Pages)} {CatalogEntries} >>");
            return Pdf.Build(Catalog);
        }

        public PdfDocument Open() => PdfDocument.Open(Build(), ParsingOptions.LenientParsingOff);
    }
}
