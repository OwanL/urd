using System.Text;
using Urd.Core.Sessions;
using Xunit;

namespace Urd.Backend.Tests.Support;

internal static class HttpTest {
  internal static StringContent JsonBody(string body) => new(body, Encoding.UTF8, "application/json");

  internal static async Task<HttpResponseMessage> RequestAsync(HttpClient client, HttpMethod method, string path,
      HttpContent? content = null, bool marker = true, string? suppliedOrigin = null, bool chunked = false) {
    using var request = new HttpRequestMessage(method, path) { Content = content };
    if (marker)
      request.Headers.Add("X-Urd-Client", "browser");
    if (suppliedOrigin is not null)
      request.Headers.Add("Origin", suppliedOrigin);
    if (chunked)
      request.Headers.TransferEncodingChunked = true;
    return await client.SendAsync(request);
  }

  internal static void AssertPrivateResponse(HttpResponseMessage response) {
    Assert.True(response.Headers.CacheControl?.NoStore);
    Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
    Assert.False(response.Headers.Contains("Access-Control-Allow-Headers"));
  }

  internal static async Task<SessionSnapshot> SettledAsync(Guid executionId,
      Func<Task<SessionSnapshot>> readSnapshot) {
    using var deadline = new CancellationTokenSource(TestAsync.TestTimeout);
    SessionSnapshot snapshot = await readSnapshot().WaitAsync(deadline.Token);
    while (snapshot.LastSettlement?.ExecutionId != executionId) {
      await Task.Delay(10, deadline.Token);
      snapshot = await readSnapshot().WaitAsync(deadline.Token);
    }
    Assert.Null(snapshot.UnsettledExecutionId);
    return snapshot;
  }
}
