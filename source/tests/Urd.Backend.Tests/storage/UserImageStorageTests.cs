using SkiaSharp;
using Urd.Backend.Tests.Support;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class UserImageStorageTests {
  [Theory]
  [InlineData(SKEncodedImageFormat.Png, "image/png")]
  [InlineData(SKEncodedImageFormat.Jpeg, "image/jpeg")]
  [InlineData(SKEncodedImageFormat.Webp, "image/webp")]
  public async Task RasterFormatsRoundTripWithAccurateMetadata(SKEncodedImageFormat format, string mediaType) {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    using var bitmap = new SKBitmap(16, 12);
    bitmap.Erase(SKColors.White);
    using var image = SKImage.FromBitmap(bitmap);
    using var encoded = image.Encode(format, 100);
    byte[] bytes = encoded.ToArray();
    var saved = await store.UserImages.SaveAsync(session, Guid.NewGuid(), bytes, default);
    var stored = store.UserImages.Read(session, saved.ImageId);
    Assert.Equal((16, 12), (saved.Width, saved.Height));
    Assert.Equal(mediaType, stored?.MediaType);
    Assert.Equal(bytes, stored!.Data.ToArray());
  }

  [Fact]
  public async Task RejectsOversizeAndNonRasterInputs() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid session = store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    await Assert.ThrowsAsync<ArgumentException>(() => store.UserImages.SaveAsync(session, Guid.NewGuid(),
        new byte[10 * 1024 * 1024 + 1], default));
    await Assert.ThrowsAsync<ArgumentException>(() => store.UserImages.SaveAsync(session, Guid.NewGuid(),
        "<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray(), default));
    using var bitmap = new SKBitmap(4_001, 4_001);
    bitmap.Erase(SKColors.White);
    using var image = SKImage.FromBitmap(bitmap);
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    await Assert.ThrowsAsync<ArgumentException>(() => store.UserImages.SaveAsync(session, Guid.NewGuid(), encoded.ToArray(), default));
  }

  [Fact]
  public async Task ImagesAreImmutableSessionOwnedAndCleanedUp() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    Guid first = Guid.NewGuid(), second = Guid.NewGuid(), id = Guid.NewGuid();
    store.Sessions.CreatePrimary(first);
    store.Sessions.CreatePrimary(second);
    using var bitmap = new SKBitmap(8, 6);
    using var image = SKImage.FromBitmap(bitmap);
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    byte[] bytes = encoded.ToArray();
    var saved = await store.UserImages.SaveAsync(first, id, bytes, default);
    Assert.Equal((8, 6), (saved.Width, saved.Height));
    Assert.Equal(saved, await store.UserImages.SaveAsync(first, id, bytes, default));
    Assert.Null(store.UserImages.Find(second, id));
    Assert.Null(store.UserImages.Read(second, id));
    Assert.NotNull(store.UserImages.Read(first, id));
    byte[] different = [.. bytes, 0];
    await Assert.ThrowsAsync<ArgumentException>(() => store.UserImages.SaveAsync(first, id, different, default));
    await Assert.ThrowsAsync<ArgumentException>(() => store.UserImages.SaveAsync(first, Guid.NewGuid(), [1, 2, 3], default));
    Assert.True(store.DeletePrimarySession(first));
    Assert.Null(store.UserImages.Find(first, id));
    Assert.Empty(Directory.EnumerateFiles(database.Path + ".user-images"));
  }
}
