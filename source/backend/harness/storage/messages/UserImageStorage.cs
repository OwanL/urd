using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Urd.Core.Sessions;
using Urd.Core.Tools;

namespace Urd.Core.Storage;

// Image IDs belong to a session and remain immutable until that session closes.
internal sealed class UserImageStorage(Database database, string directory) {
  private const int MaxSourceBytes = 10 * 1024 * 1024;
  private readonly ToolImageStore files = new(directory);

  internal async Task<UserImage> SaveAsync(Guid sessionId, Guid imageId, byte[] data, CancellationToken cancellation) {
    if (imageId == Guid.Empty)
      throw new ArgumentException("An image ID is required.", nameof(imageId));
    ArgumentNullException.ThrowIfNull(data);
    if (data.Length is 0 or > MaxSourceBytes)
      throw new ArgumentException("Image must contain at most 10 MiB.", nameof(data));
    cancellation.ThrowIfCancellationRequested();
    data = (byte[])data.Clone();
    string sourceHash = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    UserImage? retained = database.Read(transaction => CheckExisting(transaction, sessionId, imageId, sourceHash));
    if (retained is not null)
      return retained;

    ToolOutput output = await ToolImageProcessor.ProcessUserImageAsync(data, cancellation).ConfigureAwait(false);
    if (output.IsError || output.Images.Count != 1 || output.Details is null)
      throw new ArgumentException("Invalid image or image exceeds decoding limits.", nameof(data));
    int width = output.Details.Value.GetProperty("width").GetInt32();
    int height = output.Details.Value.GetProperty("height").GetInt32();
    cancellation.ThrowIfCancellationRequested();
    return database.Write(transaction => {
      if (CheckExisting(transaction, sessionId, imageId, sourceHash) is { } existing)
        return existing;
      ToolImageReference reference = files.Save(output.Images[0]);
      database.Execute(transaction, """
          INSERT INTO user_images (session_id, image_id, source_hash, file_hash, media_type, byte_length, width, height)
          VALUES ($session, $image, $source, $hash, $media, $length, $width, $height);
          """, ("$session", sessionId), ("$image", imageId), ("$source", sourceHash), ("$hash", reference.Hash),
          ("$media", reference.MediaType), ("$length", reference.ByteLength), ("$width", width), ("$height", height));
      return new UserImage(imageId, width, height);
    });
  }

  private UserImage? CheckExisting(SqliteTransaction transaction, Guid sessionId, Guid imageId, string sourceHash) {
    var retained = database.QuerySingleOrDefault(transaction, """
        SELECT source_hash, width, height FROM user_images WHERE session_id = $session AND image_id = $image;
        """, row => (Hash: row.String("source_hash"), Width: row.Int("width"), Height: row.Int("height")),
        ("$session", sessionId), ("$image", imageId));
    if (retained.Hash is null)
      return null;
    if (retained.Hash != sourceHash)
      throw new ArgumentException("Image ID is already used for different bytes.", nameof(imageId));
    return new(imageId, retained.Width, retained.Height);
  }

  internal UserImage? Find(Guid sessionId, Guid imageId) => database.Read(transaction => Find(database, transaction, sessionId, imageId));

  internal static UserImage? Find(Database database, SqliteTransaction transaction, Guid sessionId, Guid imageId) =>
      database.QuerySingleOrDefault(transaction, """
          SELECT width, height FROM user_images WHERE session_id = $session AND image_id = $image;
          """, row => new UserImage(imageId, row.Int("width"), row.Int("height")),
          ("$session", sessionId), ("$image", imageId));

  internal DataContent? Read(Guid sessionId, Guid imageId) {
    if (imageId == Guid.Empty)
      throw new ArgumentException("An image ID is required.", nameof(imageId));
    return database.Read(transaction => Read(transaction, sessionId, imageId));
  }

  internal DataContent? Read(SqliteTransaction transaction, Guid sessionId, Guid imageId) {
    ToolImageReference? reference = database.QuerySingleOrDefault(transaction, """
        SELECT file_hash, media_type, byte_length FROM user_images WHERE session_id = $session AND image_id = $image;
        """, row => new ToolImageReference(row.String("file_hash"), row.String("media_type"), row.Int("byte_length")),
        ("$session", sessionId), ("$image", imageId));
    return reference is null ? null : files.Read(reference);
  }
}
