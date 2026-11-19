using SkiaSharp;
using Urd.Core.Tools;
using Xunit;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Tools;

public sealed class ToolImageProcessorTests {
  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public async Task SmallSupportedImagePreservesBytesAndTextOnlyModelsRetainAttachment(bool acceptsImages) {
    byte[] bytes = Image(20, 10, SKEncodedImageFormat.Png);
    ToolOutput output = await Process(bytes, "image/png", acceptsImages);
    Assert.False(output.IsError);
    var image = Assert.Single(output.Images);
    Assert.Equal("image/png", image.MediaType);
    Assert.Equal(bytes, image.Data.ToArray());
    Assert.Equal(!acceptsImages, output.Text.Contains("omitted from the model request", StringComparison.Ordinal));
    Assert.False(output.Details!.Value.GetProperty("wasResized").GetBoolean());
  }

  [Fact]
  public async Task UnsupportedAndCorruptInputsFailExplicitly() {
    ToolOutput corrupt = await Process([1, 2, 3], "image/png");
    ToolOutput unsupported = await Process(Image(1, 1, SKEncodedImageFormat.Png), "image/svg+xml");
    Assert.True(corrupt.IsError);
    Assert.True(unsupported.IsError);
    Assert.Empty(corrupt.Images);
    Assert.Empty(unsupported.Images);
    using var cancelled = new CancellationTokenSource();
    await cancelled.CancelAsync();
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ToolImageProcessor.ProcessAsync(
      ReadOnlyMemory<byte>.Empty, "image/png", true, cancelled.Token).WaitAsync(TestTimeout));
  }

  private static Task<ToolOutput> Process(byte[] bytes, string mediaType, bool acceptsImages = true) =>
    ToolImageProcessor.ProcessAsync(bytes, mediaType, acceptsImages, CancellationToken.None).WaitAsync(TestTimeout);

  private static byte[] Image(int width, int height, SKEncodedImageFormat format) {
    using var bitmap = new SKBitmap(width, height);
    using (var canvas = new SKCanvas(bitmap)) {
      canvas.Clear(SKColors.Red);
      using var paint = new SKPaint { Color = SKColors.Blue };
      canvas.DrawRect(new SKRect(width / 2f, 0, width, height), paint);
    }
    using var image = SKImage.FromBitmap(bitmap);
    using var encoded = image.Encode(format, 90);
    return encoded.ToArray();
  }
}
