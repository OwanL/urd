using System.Text;
using System.Text.Json;
using Microsoft.Playwright;

namespace Urd.Core.Tools;

// Capture binary navigation responses before Chromium's PDF viewer or download manager consumes them.
// Fetch interception occurs inside the browser, after the restricted proxy has delivered the response.
internal sealed class WebNavigationCapture(ICDPSession session, string mainFrame, CancellationToken cancellation) {
  private readonly TaskCompletionSource<WebPageVisit> captured = new(TaskCreationOptions.RunContinuationsAsynchronously);

  public Task<WebPageVisit> Captured => captured.Task;

  public static async Task<WebNavigationCapture> StartAsync(IPage page, CancellationToken cancellation) {
    ICDPSession session = await page.Context.NewCDPSessionAsync(page).WaitAsync(cancellation);
    JsonElement tree = (await session.SendAsync("Page.getFrameTree").WaitAsync(cancellation))!.Value;
    string frame = tree.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString()!;
    var capture = new WebNavigationCapture(session, frame, cancellation);
    session.Event("Fetch.requestPaused").OnEvent += (sender, data) => {
      if (data.HasValue)
        _ = capture.HandleAsync(data.Value);
    };
    await session.SendAsync("Fetch.enable", new() {
      ["patterns"] = new[] { new { resourceType = "Document", requestStage = "Response" } }
    }).WaitAsync(cancellation);
    return capture;
  }

  private async Task HandleAsync(JsonElement data) {
    string id = data.GetProperty("requestId").GetString()!;
    bool isMainFrame = data.GetProperty("frameId").GetString() == mainFrame;
    try {
      if (!isMainFrame ||
          !data.TryGetProperty("responseStatusCode", out JsonElement code) ||
          !data.TryGetProperty("responseHeaders", out JsonElement headers) ||
          code.GetInt32() is >= 300 and < 400) {
        await ContinueAsync(id);
        return;
      }
      string contentType = Header(headers, "content-type");
      string disposition = Header(headers, "content-disposition");
      bool attachment = disposition.StartsWith("attachment", StringComparison.OrdinalIgnoreCase);
      if (!attachment && WebPageVisit.Transport(contentType) != WebPageVisit.WebFetchKind.RawBody) {
        await ContinueAsync(id);
        return;
      }
      JsonElement body = (await session.SendAsync("Fetch.getResponseBody", new() { ["requestId"] = id })
        .WaitAsync(cancellation))!.Value;
      string value = body.GetProperty("body").GetString() ?? "";
      byte[] bytes = body.GetProperty("base64Encoded").GetBoolean() ? Convert.FromBase64String(value) : Encoding.UTF8.GetBytes(value);
      string url = data.GetProperty("request").GetProperty("url").GetString()!;
      var visit = new WebPageVisit(url, code.GetInt32(), contentType.Length == 0 ? "application/octet-stream" : contentType,
        "", null, bytes, null, true);
      captured.TrySetResult(visit);
      await session.SendAsync("Fetch.fulfillRequest", new() {
        ["requestId"] = id, ["responseCode"] = 200,
        ["responseHeaders"] = new[] { new { name = "Content-Type", value = "text/html" } },
        ["body"] = Convert.ToBase64String("<html></html>"u8)
      }).WaitAsync(cancellation);
    } catch (Exception error) {
      if (isMainFrame)
        captured.TrySetException(error);
      try {
        await ContinueAsync(id);
      } catch (Exception) {
        // Closing the context also releases a paused request.
      }
    }
  }

  private Task<JsonElement?> ContinueAsync(string id) => session.SendAsync("Fetch.continueRequest", new() { ["requestId"] = id })
    .WaitAsync(cancellation);

  private static string Header(JsonElement headers, string name) {
    JsonElement header = headers.EnumerateArray().FirstOrDefault(item =>
      string.Equals(item.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase));
    return header.ValueKind == JsonValueKind.Undefined ? "" : header.GetProperty("value").GetString() ?? "";
  }
}
