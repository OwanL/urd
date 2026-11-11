using System.Security.Cryptography;
using Microsoft.Extensions.AI;

namespace Urd.Core.Storage;

// Immutable, content-addressed image bytes owned by one database.
internal sealed class ToolImageStore {
  private readonly string directory;

  internal ToolImageStore(string directory) {
    ArgumentException.ThrowIfNullOrWhiteSpace(directory);
    this.directory = Path.GetFullPath(directory);
  }

  internal ToolImageReference Save(DataContent image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.RawRepresentation is not null)
      throw new ArgumentException("Tool images cannot have a provider-specific representation.", nameof(image));
    if (image.Data.IsEmpty)
      throw new ArgumentException("Tool images must contain image bytes.", nameof(image));
    if (image.AdditionalProperties is { Count: > 0 })
      throw new ArgumentException("Tool images cannot have additional properties.", nameof(image));
    if (!image.HasTopLevelMediaType("image"))
      throw new ArgumentException("Tool images must use an image media type.", nameof(image));

    byte[] bytes = image.Data.ToArray();
    string hash = Hash(bytes);
    var reference = new ToolImageReference(hash, image.MediaType, bytes.Length);
    Directory.CreateDirectory(directory);
    string destination = GetPath(reference);
    string temporaryPath = Path.Combine(directory, $".{hash}.{Guid.NewGuid():N}.tmp");
    bool ownsTemporaryFile = false;

    try {
      using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
        ownsTemporaryFile = true;
        output.Write(bytes);
        output.Flush(flushToDisk: true);
      }

      try {
        File.Move(temporaryPath, destination);
        ownsTemporaryFile = false;
      } catch (IOException) when (File.Exists(destination)) {
        VerifyPublishedFile(destination, bytes);
      }

      return reference;
    } finally {
      if (ownsTemporaryFile)
        File.Delete(temporaryPath);
    }
  }

  internal DataContent Read(ToolImageReference reference) {
    ArgumentNullException.ThrowIfNull(reference);
    string path = GetPath(reference);
    EnsureRegularFile(path);
    if (new FileInfo(path).Length != reference.ByteLength)
      throw new InvalidDataException("Stored tool image length does not match its reference.");

    byte[] bytes = File.ReadAllBytes(path);
    if (bytes.Length != reference.ByteLength || Hash(bytes) != reference.Hash)
      throw new InvalidDataException("Stored tool image does not match its reference hash.");

    return new DataContent(bytes, reference.MediaType);
  }

  private string GetPath(ToolImageReference reference) {
    if (!IsSha256Hash(reference.Hash))
      throw new ArgumentException("Tool image references require a lowercase SHA-256 hash.", nameof(reference));
    if (reference.ByteLength < 0)
      throw new ArgumentException("Tool image references cannot have a negative byte length.", nameof(reference));
    if (!IsImageMediaType(reference.MediaType))
      throw new ArgumentException("Tool image references require an image media type.", nameof(reference));

    // A fixed-length hexadecimal filename cannot escape the store directory.
    return Path.Combine(directory, reference.Hash);
  }

  private static void VerifyPublishedFile(string path, byte[] expectedBytes) {
    EnsureRegularFile(path);
    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    if (input.Length != expectedBytes.Length)
      throw new InvalidDataException("A published tool image with the same hash has a different length.");

    byte[] buffer = new byte[81920];
    int offset = 0;
    while (offset < expectedBytes.Length) {
      int count = input.Read(buffer, 0, Math.Min(buffer.Length, expectedBytes.Length - offset));
      if (count == 0 || !buffer.AsSpan(0, count).SequenceEqual(expectedBytes.AsSpan(offset, count)))
        throw new InvalidDataException("A published tool image with the same hash has different bytes.");
      offset += count;
    }
    if (input.ReadByte() != -1)
      throw new InvalidDataException("A published tool image with the same hash has a different length.");
  }

  private static void EnsureRegularFile(string path) {
    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
      throw new InvalidDataException("Stored tool images cannot be symbolic links or reparse points.");
  }

  private static string Hash(ReadOnlySpan<byte> bytes) =>
    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

  private static bool IsSha256Hash(string hash) => hash is { Length: 64 } &&
      hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

  private static bool IsImageMediaType(string mediaType) {
    try {
      return new DataContent(ReadOnlyMemory<byte>.Empty, mediaType).HasTopLevelMediaType("image");
    } catch (ArgumentException) {
      return false;
    }
  }
}

// Ordered image metadata stored in a tool result, never a model-facing path.
internal sealed record ToolImageReference(string Hash, string MediaType, int ByteLength);
