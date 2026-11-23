using System.Net;
using System.Net.Http.Json;
using SkiaSharp;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Xunit;
using static Urd.Backend.Tests.Support.HttpTest;

namespace Urd.Backend.Tests.Hosting;

public sealed class SessionUserImageRoutesTests {
  [Fact]
  public async Task ImageRoundTripIsPrivateSessionScopedAndIdempotent() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();
    HttpClient client = server.Client;
    Guid sessionId = await CreateSessionAsync(client);
    Guid other = await CreateSessionAsync(client);
    Guid imageId = Guid.NewGuid();
    string path = $"/api/sessions/{sessionId}/user-images/{imageId}";
    byte[] png = CreatePng(8);

    using (HttpResponseMessage denied = await RequestAsync(client, HttpMethod.Put, path, ImageBody(png), marker: false)) {
      Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
      AssertPrivateResponse(denied);
    }
    for (int i = 0; i < 2; i++) {
      using HttpResponseMessage upload = await RequestAsync(client, HttpMethod.Put, path, ImageBody(png));
      Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
      Assert.Equal(new UserImage(imageId, 8, 6), await upload.Content.ReadFromJsonAsync<UserImage>());
    }
    using (HttpResponseMessage conflict = await RequestAsync(client, HttpMethod.Put, path, ImageBody(CreatePng(9))))
      Assert.Equal(HttpStatusCode.BadRequest, conflict.StatusCode);
    using (HttpResponseMessage foreign = await RequestAsync(client, HttpMethod.Get, $"/api/sessions/{other}/user-images/{imageId}"))
      Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    using (HttpResponseMessage denied = await RequestAsync(client, HttpMethod.Get, path, marker: false))
      Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    using HttpResponseMessage read = await RequestAsync(client, HttpMethod.Get, path);
    Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    AssertPrivateResponse(read);
    Assert.Equal("image/png", read.Content.Headers.ContentType?.MediaType);
    using SKBitmap bitmap = SKBitmap.Decode(await read.Content.ReadAsByteArrayAsync());
    Assert.Equal((8, 6), (bitmap.Width, bitmap.Height));
  }

  [Fact]
  public async Task AClipboardImageCanExceedTheOrdinaryJsonBodyLimit() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();
    Guid sessionId = await CreateSessionAsync(server.Client);
    Guid imageId = Guid.NewGuid();
    using var bitmap = new SKBitmap(256, 256, SKColorType.Bgra8888, SKAlphaType.Opaque);
    new Random(0).NextBytes(bitmap.GetPixelSpan());
    using var image = SKImage.FromBitmap(bitmap);
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    Assert.True(encoded.Size > 128 * 1024);

    using HttpResponseMessage upload = await RequestAsync(server.Client, HttpMethod.Put,
        $"/api/sessions/{sessionId}/user-images/{imageId}", ImageBody(encoded.ToArray()));
    Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
    Assert.Equal(new UserImage(imageId, 256, 256), await upload.Content.ReadFromJsonAsync<UserImage>());
  }

  [Fact]
  public async Task UploadRejectsNonImagesAndOversizedBodiesWithOrWithoutContentLength() {
    using var database = new TemporaryDatabase();
    await using var server = new RunningServer(RunningServer.OptionsFor(database));
    await server.WaitForReadyAsync();
    HttpClient client = server.Client;
    Guid sessionId = await CreateSessionAsync(client);
    string path = $"/api/sessions/{sessionId}/user-images/{Guid.NewGuid()}";

    using (HttpResponseMessage json = await RequestAsync(client, HttpMethod.Put, path, JsonBody("{}")))
      Assert.Equal(HttpStatusCode.UnsupportedMediaType, json.StatusCode);
    using (HttpResponseMessage invalid = await RequestAsync(client, HttpMethod.Put, path, ImageBody([1, 2, 3])))
      Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    byte[] oversized = new byte[10 * 1024 * 1024 + 1];
    using (var request = new HttpRequestMessage(HttpMethod.Put, path) { Content = ImageBody(oversized) }) {
      request.Headers.Add("X-Urd-Client", "browser");
      request.Headers.ExpectContinue = true;
      using HttpResponseMessage large = await client.SendAsync(request);
      Assert.Equal(HttpStatusCode.RequestEntityTooLarge, large.StatusCode);
    }
    using (HttpResponseMessage chunked = await RequestAsync(client, HttpMethod.Put, path, ImageBody(oversized), chunked: true))
      Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode);
    using HttpResponseMessage absent = await RequestAsync(client, HttpMethod.Get, path);
    Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
  }

  private static async Task<Guid> CreateSessionAsync(HttpClient client) {
    using HttpResponseMessage created = await RequestAsync(client, HttpMethod.Post, "/api/sessions", JsonBody("{}"));
    Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    return Assert.IsType<CreatedSession>(await created.Content.ReadFromJsonAsync<CreatedSession>()).SessionId;
  }

  private static byte[] CreatePng(int width) {
    using var bitmap = new SKBitmap(width, 6);
    using var image = SKImage.FromBitmap(bitmap);
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    return encoded.ToArray();
  }

  private static ByteArrayContent ImageBody(byte[] data) {
    var body = new ByteArrayContent(data);
    body.Headers.ContentType = new("image/png");
    return body;
  }

  private sealed record CreatedSession(Guid SessionId);
}
