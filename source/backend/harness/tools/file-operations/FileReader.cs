namespace Urd.Core.Tools;

// Detects file content and dispatches to bounded text or image reading.
internal static class FileReader {
  private const int maxImageSourceBytes = 64 * 1024 * 1024;
  private static readonly SemaphoreSlim imageReads = new(2);
  private static readonly byte[] jpegSignature = [0xFF, 0xD8, 0xFF];
  private static readonly byte[] pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

  public static async Task<ToolOutput> ReadAsync(string path, int offset, int limit,
      bool modelAcceptsImages, CancellationToken cancellation) {
    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
      8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
    byte[] header = new byte[32];
    int headerLength = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellation).ConfigureAwait(false);
    string? mime = ImageMime(header.AsSpan(0, headerLength));
    stream.Position = 0;
    if (mime is null)
      return await TextFilePage.ReadAsync(stream, path, offset, limit, cancellation).ConfigureAwait(false);
    return await ReadImageAsync(stream, mime, modelAcceptsImages, cancellation).ConfigureAwait(false);
  }

  private static async Task<ToolOutput> ReadImageAsync(FileStream stream, string mime, bool modelAcceptsImages,
      CancellationToken cancellation) {
    if (stream.Length > maxImageSourceBytes)
      return new ToolOutput("[Image omitted: source exceeds the 64MiB image processing limit.]", [], IsError: true);
    // Acquire before buffering, so queued image reads do not retain source-sized arrays.
    await imageReads.WaitAsync(cancellation).ConfigureAwait(false);
    try {
      using var image = new MemoryStream();
      byte[] buffer = new byte[8192];
      while (true) {
        int count = await stream.ReadAsync(buffer, cancellation).ConfigureAwait(false);
        if (count == 0)
          break;
        // An outside writer may grow the file after the length check.
        if (image.Length + count > maxImageSourceBytes)
          return new ToolOutput("[Image omitted: source exceeds the 64MiB image processing limit.]", [], IsError: true);
        await image.WriteAsync(buffer.AsMemory(0, count), cancellation).ConfigureAwait(false);
      }
      cancellation.ThrowIfCancellationRequested();
      return await ToolImageProcessor.ProcessAsync(image.GetBuffer().AsMemory(0, (int)image.Length),
        mime, modelAcceptsImages, cancellation).ConfigureAwait(false);
    } finally {
      imageReads.Release();
    }
  }

  private static string? ImageMime(ReadOnlySpan<byte> header) {
    if (header.StartsWith(jpegSignature) && header.Length >= 4 && header[3] != 0xF7)
      return "image/jpeg";
    if (header.StartsWith(pngSignature) && header.Length >= 16 && header[12..16].SequenceEqual("IHDR"u8))
      return "image/png";
    if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8))
      return "image/gif";
    if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8))
      return "image/webp";
    if (header.Length >= 26 && header.StartsWith("BM"u8)) {
      uint dibSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[14..]);
      int planesOffset = dibSize == 12 ? 22 : 26;
      if ((dibSize == 12 || dibSize is >= 40 and <= 124) && header.Length >= planesOffset + 4 &&
          System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[planesOffset..]) == 1 &&
          System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(header[(planesOffset + 2)..]) is 1 or 4 or 8 or 16 or 24 or 32)
        return "image/bmp";
    }
    return null;
  }
}
