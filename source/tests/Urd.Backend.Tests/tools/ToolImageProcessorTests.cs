using System.Buffers.Binary;
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
  public async Task LargeExifImageIsOrientedAndResizedWithoutLyingAboutCoordinates() {
    byte[] jpeg = Image(3000, 1000, SKEncodedImageFormat.Jpeg);
    // APP1: big-endian EXIF orientation 6 (90 degrees clockwise).
    byte[] exif = [0xff, 0xe1, 0, 0x22, 0x45, 0x78, 0x69, 0x66, 0, 0,
      0x4d, 0x4d, 0, 0x2a, 0, 0, 0, 8, 0, 1, 1, 0x12, 0, 3, 0, 0, 0, 1, 0, 6, 0, 0, 0, 0, 0, 0];
    byte[] oriented = [.. jpeg.AsSpan(0, 2), .. exif, .. jpeg.AsSpan(2)];
    ToolOutput output = await Process(oriented, "image/jpeg");
    Assert.False(output.IsError);
    var image = Assert.Single(output.Images);
    using var result = SKBitmap.Decode(image.Data.ToArray());
    Assert.Equal(667, result.Width);
    Assert.Equal(2000, result.Height);
    SKColor top = result.GetPixel(result.Width / 2, 100);
    SKColor bottom = result.GetPixel(result.Width / 2, result.Height - 100);
    Assert.True(top.Red > top.Blue);
    Assert.True(bottom.Blue > bottom.Red);
    Assert.Equal(1000, output.Details!.Value.GetProperty("originalWidth").GetInt32());
    Assert.Equal(3000, output.Details.Value.GetProperty("originalHeight").GetInt32());
    Assert.True(output.Details.Value.GetProperty("orientationApplied").GetBoolean());
    Assert.Contains("map displayed coordinates", output.Text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task BmpIsConvertedToSupportedPng() {
    byte[] bmp = new byte[58];
    bmp[0] = (byte)'B';
    bmp[1] = (byte)'M';
    BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
    BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), 54);
    BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(14), 40);
    BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(18), 1);
    BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(22), 1);
    BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(26), 1);
    BinaryPrimitives.WriteInt16LittleEndian(bmp.AsSpan(28), 24);
    bmp[54] = 255;
    ToolOutput output = await Process(bmp, "image/bmp");
    Assert.False(output.IsError);
    var image = Assert.Single(output.Images);
    Assert.Equal("image/png", image.MediaType);
    using var result = SKBitmap.Decode(image.Data.ToArray());
    Assert.Equal(SKColors.Blue, result.GetPixel(0, 0));
  }

  [Theory]
  [InlineData(3538941, false)]
  [InlineData(3538944, true)]
  public async Task InlineLimitCountsBase64AndExcludesTheExactBoundary(int bytes, bool transformed) {
    byte[] png = Image(2, 2, SKEncodedImageFormat.Png);
    byte[] padded = new byte[bytes];
    png.CopyTo(padded, 0); // Legal trailing data makes the byte boundary independent of compression.
    ToolOutput output = await Process(padded, "image/png");
    Assert.False(output.IsError);
    var image = Assert.Single(output.Images);
    Assert.True(4L * ((image.Data.Length + 2L) / 3) < 4718592);
    Assert.Equal(transformed, output.Details!.Value.GetProperty("wasTransformed").GetBoolean());
    if (!transformed)
      Assert.Equal(padded, image.Data.ToArray());
  }

  [Fact]
  public async Task OversizedPngFallsBackToJpegBeforeShrinkingDimensions() {
    byte[] png = Image(1300, 1000, SKEncodedImageFormat.Png, noise: true);
    Assert.True(4L * ((png.Length + 2L) / 3) >= 4718592);
    ToolOutput output = await Process(png, "image/png");
    Assert.False(output.IsError);
    var image = Assert.Single(output.Images);
    Assert.Equal("image/jpeg", image.MediaType);
    Assert.True(4L * ((image.Data.Length + 2L) / 3) < 4718592);
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

  private static byte[] Image(int width, int height, SKEncodedImageFormat format, bool noise = false) {
    using var bitmap = new SKBitmap(width, height);
    using (var canvas = new SKCanvas(bitmap)) {
      canvas.Clear(SKColors.Red);
      using var paint = new SKPaint { Color = SKColors.Blue };
      canvas.DrawRect(new SKRect(width / 2f, 0, width, height), paint);
    }
    if (noise) {
      var random = new Random(17);
      var pixels = new SKColor[width * height];
      for (int index = 0; index < pixels.Length; index++)
        pixels[index] = new SKColor((uint)random.Next(0x1000000) | 0xff000000);
      bitmap.Pixels = pixels;
    }
    using var image = SKImage.FromBitmap(bitmap);
    using var encoded = image.Encode(format, 90);
    return encoded.ToArray();
  }
}
