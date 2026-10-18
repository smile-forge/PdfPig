namespace UglyToad.PdfPig.Tests.Graphics
{
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using Xunit;

    /// <summary>
    /// A form XObject runs between a saved and a restored graphics state, so its content cannot change the state it was
    /// drawn in: a <c>q</c> it leaves open does not outlive it, and a <c>Q</c> past its own does not restore its caller's.
    /// A header band exported with an unclosed <c>q</c> under 26 % alpha otherwise left the rest of its page faded.
    /// </summary>
    public class FormStateBalanceTests
    {
        [Fact]
        public void AStateAFormLeavesSaved_DoesNotOutliveIt()
        {
            var colour = ColourAfter("q");
            Assert.Equal((0, 0, 1), colour);
        }

        [Fact]
        public void ARestoreAFormDoesNotOwe_DoesNotRestoreItsCallersState()
        {
            var colour = ColourAfter("Q Q");
            Assert.Equal((0, 0, 1), colour);
        }

        /// <summary>The colour of text drawn after a page sets blue, saves, sets red and draws a form of <paramref name="form"/>, then restores.</summary>
        private static (double, double, double) ColourAfter(string form)
        {
            var page = "0 0 1 rg q 1 0 0 rg /Fm Do Q BT /F 12 Tf 20 20 Td (after) Tj ET";
            using var document = PdfDocument.Open(Pdf(page, form));
            var letter = document.GetPage(1).Letters.First();
            return letter.FillColor.ToRGBValues();
        }

        private static byte[] Pdf(string page, string form)
        {
            var objects = new List<string>
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F 5 0 R >> /XObject << /Fm 6 0 R >> >> /Contents 4 0 R >>",
                $"<< /Length {page.Length} >>\nstream\n{page}\nendstream",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
                $"<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Length {form.Length} >>\nstream\n{form}\nendstream",
            };

            var pdf = new StringBuilder("%PDF-1.7\n");
            var offsets = new List<int>();
            for (var i = 0; i < objects.Count; i++)
            {
                offsets.Add(pdf.Length);
                pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
            }

            var xref = pdf.Length;
            pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets)
            {
                pdf.Append($"{offset:D10} 00000 n \n");
            }

            pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return Encoding.ASCII.GetBytes(pdf.ToString());
        }
    }
}
