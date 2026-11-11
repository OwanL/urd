using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SkiaSharp;

namespace Urd.Core.Tools;

// Produces bounded inline images; native decoding and encoding have two shared slots.
internal static class ToolImageProcessor {
  private const int maxDimension = 2000;
  private const long maxBase64Bytes = 4718592;
  private const long maxDecodedPixels = 16_000_000;
  private static readonly SemaphoreSlim slots = new(2);
  private static readonly int[] jpegQualities = [80, 85, 70, 55, 40];

  internal static async Task<ToolOutput> ProcessAsync(ReadOnlyMemory<byte> bytes, string mediaType,
      bool modelAcceptsImages, CancellationToken cancellation) {
    await slots.WaitAsync(cancellation).ConfigureAwait(false);
    try {
      return await Task.Run(() => Process(bytes, mediaType, modelAcceptsImages, cancellation), cancellation).ConfigureAwait(false);
    } finally {
      slots.Release();
    }
  }

  private static ToolOutput Process(ReadOnlyMemory<byte> bytes, string mediaType,
      bool modelAcceptsImages, CancellationToken cancellation) {
    cancellation.ThrowIfCancellationRequested();
    string declared = mediaType.Split(';')[0].Trim().ToLowerInvariant();
    if (declared == "image/jpg")
      declared = "image/jpeg";
    if (declared is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp"))
      return Error("unsupported image format.");
    try {
      using var data = SKData.CreateCopy(bytes.Span);
      using var codec = SKCodec.Create(data);
      if (codec is null)
        return Error("invalid or unsupported image data.");
      return ProcessCodec(codec, bytes, declared, modelAcceptsImages, cancellation);
    } catch (Exception error) when (error is ArgumentException or InvalidOperationException or ExternalException or OverflowException or NotSupportedException) {
      return Error("image conversion failed.");
    } catch (Exception error) when (error is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException) {
      return Error("image processing is unavailable on this host.");
    }
  }

  private static ToolOutput ProcessCodec(SKCodec codec, ReadOnlyMemory<byte> bytes, string declared,
      bool modelAcceptsImages, CancellationToken cancellation) {
    string? detected = codec.EncodedFormat switch {
      SKEncodedImageFormat.Png => "image/png",
      SKEncodedImageFormat.Jpeg => "image/jpeg",
      SKEncodedImageFormat.Gif => "image/gif",
      SKEncodedImageFormat.Webp => "image/webp",
      SKEncodedImageFormat.Bmp => "image/bmp",
      _ => null
    };
    if (detected is null || detected != declared)
      return Error("image data does not match its supported media type.");
    SKImageInfo info = codec.Info;
    if (info.Width <= 0 || info.Height <= 0)
      return Error("invalid image dimensions.");
    bool swapsAxes = codec.EncodedOrigin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
      or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
    var original = swapsAxes ? new SKSizeI(info.Height, info.Width) : new SKSizeI(info.Width, info.Height);
    SKSizeI target = Fit(original);
    // JPEG supports native downsampling. Other codecs may return the full size here.
    SKSizeI decodeSize = codec.GetScaledDimensions(Math.Min(1f, (float)maxDimension / Math.Max(info.Width, info.Height)));
    if (decodeSize.Width <= 0 || decodeSize.Height <= 0 || (long)decodeSize.Width * decodeSize.Height > maxDecodedPixels)
      return Error("image exceeds the 16-million-pixel decode limit after supported downsampling.");
    using var decoded = new SKBitmap();
    if (!decoded.TryAllocPixels(new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul)))
      return Error("could not allocate image pixels.");
    cancellation.ThrowIfCancellationRequested();
    if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
      return Error("could not decode the complete image.");
    cancellation.ThrowIfCancellationRequested();
    if (detected != "image/bmp" && original == target && Fits(bytes.Length))
      return Output(bytes, detected, detected, original, original, transformed: false, orientationApplied: false, modelAcceptsImages);
    return Transform(decoded, codec.EncodedOrigin, detected, original, target, modelAcceptsImages, cancellation);
  }

  private static ToolOutput Transform(SKBitmap decoded, SKEncodedOrigin origin, string originalMediaType,
      SKSizeI original, SKSizeI target, bool modelAcceptsImages, CancellationToken cancellation) {
    using var source = SKImage.FromBitmap(decoded);
    if (source is null)
      return Error("could not create the decoded image.");
    for (SKSizeI size = target; size.Width > 0 && size.Height > 0; size = Smaller(size)) {
      cancellation.ThrowIfCancellationRequested();
      using var rendered = new SKBitmap();
      if (!rendered.TryAllocPixels(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul)))
        return Error("could not allocate the displayed image.");
      using (var canvas = new SKCanvas(rendered)) {
        bool swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
          or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale((float)size.Width / (swapsAxes ? decoded.Height : decoded.Width),
          (float)size.Height / (swapsAxes ? decoded.Width : decoded.Height));
        SKMatrix orientation = Orientation(origin, decoded.Width, decoded.Height);
        canvas.Concat(in orientation);
        canvas.DrawImage(source, 0, 0, new SKSamplingOptions(SKCubicResampler.Mitchell));
      }
      using var image = SKImage.FromBitmap(rendered);
      if (image is null)
        return Error("could not create the displayed image.");
      if (Encode(image, cancellation) is { } candidate)
        return Output(candidate.Bytes, candidate.MediaType, originalMediaType, original, size, transformed: true,
          orientationApplied: origin != SKEncodedOrigin.TopLeft, modelAcceptsImages);
    }
    return Error($"could not convert {originalMediaType} below the inline image limit.");
  }

  private static (byte[] Bytes, string MediaType)? Encode(SKImage image, CancellationToken cancellation) {
    cancellation.ThrowIfCancellationRequested();
    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
    if (png is not null && Fits(png.Size))
      return (png.ToArray(), "image/png");
    foreach (int quality in jpegQualities) {
      cancellation.ThrowIfCancellationRequested();
      using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, quality);
      if (jpeg is not null && Fits(jpeg.Size))
        return (jpeg.ToArray(), "image/jpeg");
    }
    return null;
  }

  private static SKSizeI Fit(SKSizeI original) {
    int width = original.Width, height = original.Height;
    if (width > maxDimension) {
      height = Math.Max(1, (int)Math.Round((double)height * maxDimension / width, MidpointRounding.AwayFromZero));
      width = maxDimension;
    }
    if (height > maxDimension) {
      width = Math.Max(1, (int)Math.Round((double)width * maxDimension / height, MidpointRounding.AwayFromZero));
      height = maxDimension;
    }
    return new(width, height);
  }

  private static SKSizeI Smaller(SKSizeI size) => size.Width == 1 && size.Height == 1
    ? new(0, 0) : new(Math.Max(1, size.Width * 3 / 4), Math.Max(1, size.Height * 3 / 4));

  private static bool Fits(long bytes) => 4 * ((bytes + 2) / 3) < maxBase64Bytes;

  private static SKMatrix Orientation(SKEncodedOrigin origin, int width, int height) => origin switch {
    SKEncodedOrigin.TopRight => new(-1, 0, width, 0, 1, 0, 0, 0, 1),
    SKEncodedOrigin.BottomRight => new(-1, 0, width, 0, -1, height, 0, 0, 1),
    SKEncodedOrigin.BottomLeft => new(1, 0, 0, 0, -1, height, 0, 0, 1),
    SKEncodedOrigin.LeftTop => new(0, 1, 0, 1, 0, 0, 0, 0, 1),
    SKEncodedOrigin.RightTop => new(0, -1, height, 1, 0, 0, 0, 0, 1),
    SKEncodedOrigin.RightBottom => new(0, -1, height, -1, 0, width, 0, 0, 1),
    SKEncodedOrigin.LeftBottom => new(0, 1, 0, -1, 0, width, 0, 0, 1),
    _ => SKMatrix.Identity
  };

  private static ToolOutput Output(ReadOnlyMemory<byte> bytes, string mediaType, string originalMediaType,
      SKSizeI original, SKSizeI displayed, bool transformed, bool orientationApplied, bool modelAcceptsImages) {
    bool resized = original != displayed;
    string note = $"Read image file [{mediaType}]\n[Original oriented dimensions: {original.Width}×{original.Height}; displayed: {displayed.Width}×{displayed.Height}.]";
    if (resized)
      note += string.Create(CultureInfo.InvariantCulture, $"\n[To map displayed coordinates to the original oriented image, multiply x by {(double)original.Width / displayed.Width:0.###} and y by {(double)original.Height / displayed.Height:0.###}.]");
    if (originalMediaType != mediaType)
      note += $"\n[Image converted from {originalMediaType} to {mediaType}.]";
    else if (transformed)
      note += "\n[Image re-encoded.]";
    if (transformed && originalMediaType is "image/gif" or "image/webp")
      note += "\n[Image transformation uses frame 0.]";
    if (orientationApplied)
      note += "\n[EXIF orientation applied.]";
    if (!modelAcceptsImages)
      note += "\n[Current model does not support images. Image retained, but omitted from the model request.]";
    return new(note, [new DataContent(bytes, mediaType)], JsonSerializer.SerializeToElement(new {
      originalWidth = original.Width, originalHeight = original.Height,
      width = displayed.Width, height = displayed.Height, wasResized = resized,
      wasTransformed = transformed, orientationApplied
    }));
  }

  private static ToolOutput Error(string reason) => new($"[Image omitted: {reason}]", [], IsError: true);
}
