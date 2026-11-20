using Microsoft.Extensions.AI;
using SkiaSharp;
using Urd.Core.Execution;
using Urd.Core.MarkdownRendering;
using Urd.Core.Sessions;
using Urd.Core.Tools;

namespace Urd.Core.Storage;

// The importer is the only consumer of destination paths. Reads use committed message references.
internal sealed class MarkdownImageStorage(Database database, string directory) {
  private const int MaxSourceBytes = 8 * 1024 * 1024;
  private const int MaxSide = 8192;
  private const long MaxPixels = 16_000_000;
  private readonly ToolImageStore files = new(directory);

  internal void SaveImage(ExecutionIdentity execution, Guid responseId, MarkdownImageTarget target,
      string? workingDirectory) {
    DataContent? image;
    try {
      image = Capture(target.Destination, workingDirectory);
    } catch (Exception failure) when (UnavailableFile(failure)) {
      return;
    }
    if (image is null)
      return;

    database.Write(transaction => {
      long? ordinal = database.Scalar<long?>(transaction, $"""
        SELECT m.ordinal FROM execution_messages m JOIN sessions s ON s.session_id = m.session_id
        WHERE m.session_id = $session AND m.run_id = $run AND m.message_id = $message
            AND m.role = {(int)TranscriptEntryKind.Text} AND m.history_revision = $revision AND s.history_revision = $revision;
        """, ExecutionSql.Parameters(execution, ("$message", responseId)));
      if (ordinal is null)
        return 0;

      ToolImageReference reference;
      try {
        reference = files.Save(image);
      } catch (Exception failure) when (UnavailableFile(failure)) {
        return 0;
      }
      return database.Execute(transaction, """
        INSERT INTO markdown_images (message_ordinal, image_key, file_hash, media_type, byte_length)
        VALUES ($ordinal, $key, $hash, $media, $length) ON CONFLICT DO NOTHING;
        """, ("$ordinal", ordinal), ("$key", target.Key), ("$hash", reference.Hash),
          ("$media", reference.MediaType), ("$length", reference.ByteLength));
    });
  }

  internal DataContent? Read(Guid sessionId, MessageRef message, string key) {
    ToolImageReference? reference = database.Read(transaction => database.QuerySingleOrDefault(transaction, $"""
      SELECT i.file_hash, i.media_type, i.byte_length
      FROM execution_messages m JOIN sessions s ON s.session_id = m.session_id
      JOIN markdown_images i ON i.message_ordinal = m.ordinal
      WHERE m.session_id = $session AND m.run_id = $run AND m.message_id = $message
          AND m.role = {(int)TranscriptEntryKind.Text} AND m.history_revision = $revision AND s.history_revision = $revision
          AND i.image_key = $key;
      """, row => new ToolImageReference(row.String("file_hash"), row.String("media_type"), row.Int("byte_length")),
          ("$session", sessionId), ("$run", message.ExecutionId), ("$message", message.MessageId),
          ("$revision", message.HistoryRevision), ("$key", key)));
    try {
      return reference is null ? null : files.Read(reference);
    } catch (Exception failure) when (UnavailableFile(failure)) {
      return null;
    }
  }

  private static DataContent? Capture(string destination, string? workingDirectory) {
    destination = ToolPaths.WindowsDrivePath(destination);
    if (!Path.IsPathFullyQualified(destination) && (workingDirectory is null || Path.IsPathRooted(destination)))
      return null;
    string path = ToolPaths.ResolveLexically(destination, workingDirectory ?? Path.GetPathRoot(destination)!);
    if (!IsLocal(path))
      return null;
    path = ToolPaths.Resolve(path, path);
    if (!IsLocal(path) || new FileInfo(path).Length is <= 0 or > MaxSourceBytes)
      return null;

    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        8192, FileOptions.SequentialScan);
    if (OpenedFilePath.Resolve(input.SafeFileHandle) is not { } opened || !IsLocal(opened)
        || input.Length is <= 0 or > MaxSourceBytes)
      return null;
    var bytes = new byte[(int)input.Length];
    input.ReadExactly(bytes);
    if (input.ReadByte() != -1)
      return null;
    using var data = SKData.CreateCopy(bytes);
    using var codec = SKCodec.Create(data);
    if (codec is null || codec.Info.Width is <= 0 or > MaxSide || codec.Info.Height is <= 0 or > MaxSide
        || (long)codec.Info.Width * codec.Info.Height > MaxPixels)
      return null;
    string? mime = codec.EncodedFormat switch {
      SKEncodedImageFormat.Png => "image/png",
      SKEncodedImageFormat.Jpeg => "image/jpeg",
      SKEncodedImageFormat.Webp => "image/webp",
      _ => null
    };
    return mime is null ? null : new DataContent(bytes, mime);
  }

  private static bool IsLocal(string path) => !OperatingSystem.IsWindows()
      || (!path.StartsWith("\\\\", StringComparison.Ordinal) && new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Network);

  private static bool UnavailableFile(Exception failure) => failure is IOException or InvalidDataException
      or UnauthorizedAccessException or ArgumentException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException
      or TypeInitializationException;
}
