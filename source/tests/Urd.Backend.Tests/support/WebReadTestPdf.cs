using System.Text;

namespace Urd.Backend.Tests.Support;

// Builds minimal valid PDF bytes for deterministic text-layer extraction tests.
internal static class WebReadTestPdf {
  public static byte[] WithTextLayer(string textOperation = "BT /F1 12 Tf 72 720 Td (Hello web read) Tj ET") =>
    Build(textOperation);

  public static byte[] WithoutTextLayer() => Build("");

  private static byte[] Build(string content) {
    var objects = new Dictionary<int, string> {
      { 1, "<< /Type /Catalog /Pages 2 0 R >>" },
      { 2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" },
      { 3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>" },
      { 4, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream" },
      { 5, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" },
    };
    var buffer = new List<byte>();
    void Raw(string line) => buffer.AddRange(Encoding.ASCII.GetBytes(line));
    Raw("%PDF-1.4\n");
    var offsets = new Dictionary<int, long>();
    foreach ((int id, string body) in objects) {
      offsets[id] = buffer.Count;
      Raw($"{id} 0 obj\n{body}\nendobj\n");
    }
    var table = buffer.Count;
    Raw($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
    foreach (int id in objects.Keys)
      Raw($"{offsets[id]:D10} 00000 n \n");
    Raw($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{table}\n%%EOF\n");
    return buffer.ToArray();
  }
}
