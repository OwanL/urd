using Microsoft.Extensions.AI;
using SkiaSharp;
using Urd.Backend.Tests.Support;
using Urd.Core.Sessions;
using Xunit;
using static Urd.Backend.Tests.Support.ScriptedResponses;
using static Urd.Backend.Tests.Support.TestAsync;

namespace Urd.Backend.Tests.Sessions;

public sealed class UserImageIntegrationTests {
  [Fact]
  public async Task ImageOnlySubmissionRetriesByOrderedValueAndReconstructsFutureContext() {
    using var client = new ScriptedChatClient(Complete);
    await using var fixture = new ExecutionFixture(client);
    var session = fixture.Session;
    UserImage first = await SaveImageAsync(session, 8, 6);
    UserImage second = await SaveImageAsync(session, 9, 7);
    var selection = TestLlmSelection.Default with { SupportsImages = true };
    var input = new UserMessageSubmission(Guid.NewGuid(), 0, "", selection, [first.ImageId, second.ImageId]);
    UserMessageReceipt accepted = await session.SubmitUserMessageAsync(input);
    Assert.Equal(MessageAcceptance.Accepted, accepted.Acceptance);
    await session.WaitForExecutionFinishAsync(accepted.ExecutionId!.Value).WaitAsync(TestTimeout);
    UserMessageReceipt retry = await session.SubmitUserMessageAsync(input with { ImageIds = new[] { first.ImageId, second.ImageId } });
    Assert.Equal(accepted.ExecutionId, retry.ExecutionId);
    Assert.Equal(MessageAcceptance.Accepted, retry.Acceptance);
    await Assert.ThrowsAsync<UserMessageConflictException>(() => session.SubmitUserMessageAsync(
        input with { ImageIds = new[] { second.ImageId, first.ImageId } }));
    await Assert.ThrowsAsync<UserMessageConflictException>(() => session.SubmitUserMessageAsync(
        input with { ImageIds = new[] { first.ImageId } }));

    var page = await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0));
    var user = Assert.Single(page.Entries, item => item.Entry.Kind == TranscriptEntryKind.User);
    Assert.Equal(new[] { first, second }, user.Images);
    var positioned = await session.Transcript.ReadEntryPositionAsync(0, user.Entry);
    Assert.Equal(new[] { first, second }, Assert.Single(positioned.Entries, item => item.Descriptor.Entry == user.Entry).Descriptor.Images);
    var imageOnlyMessage = Assert.Single(client.Requests[0], message => message.Role == ChatRole.User);
    Assert.All(imageOnlyMessage.Contents, content => Assert.IsType<DataContent>(content));
    await AssertImageContentsAsync(session, imageOnlyMessage, first, second);
    UserMessageReceipt next = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "next", selection));
    await session.WaitForExecutionFinishAsync(next.ExecutionId!.Value).WaitAsync(TestTimeout);
    ChatMessage previous = Assert.Single(client.LastRequest, message => message.Role == ChatRole.User && message.Contents.OfType<DataContent>().Any());
    await AssertImageContentsAsync(session, previous, first, second);
  }

  [Fact]
  public async Task InvalidSelectionAndForeignImagesAreRejectedWithoutLosingSavedImages() {
    using var client = new ScriptedChatClient(Complete);
    await using var fixture = new ExecutionFixture(client);
    var session = fixture.Session;
    UserImage image = await SaveImageAsync(session, 8, 6);
    Guid foreignSession = fixture.Store.Sessions.CreatePrimary(Guid.NewGuid()).Id;
    Guid foreignId = Guid.NewGuid();
    await fixture.Store.UserImages.SaveAsync(foreignSession, foreignId, ImageBytes(9, 7), default);
    var selection = TestLlmSelection.Default with { SupportsImages = true };
    await Assert.ThrowsAsync<ArgumentException>(() => session.SubmitUserMessageAsync(
        new(Guid.NewGuid(), 0, "text", selection with { SupportsImages = false }, [image.ImageId])));
    await Assert.ThrowsAsync<ArgumentException>(() => session.SubmitUserMessageAsync(
        new(Guid.NewGuid(), 0, "text", selection, [foreignId])));
    await Assert.ThrowsAsync<ArgumentException>(() => session.SubmitUserMessageAsync(
        new(Guid.NewGuid(), 0, "text", selection, Enumerable.Repeat(image.ImageId, 9).ToArray())));
    await Assert.ThrowsAsync<ArgumentException>(() => session.SubmitUserMessageAsync(
        new(Guid.NewGuid(), 0, "text", selection, [image.ImageId, image.ImageId])));
    Assert.Null(await session.ReadUserImageAsync(foreignId));
    Assert.NotNull(await session.ReadUserImageAsync(image.ImageId));
    Assert.Equal(0, client.Calls);
  }

  [Fact]
  public async Task RefusedSubmissionRetainsImagesForExactRetryAndResubmit() {
    using var client = new ScriptedChatClient(Complete);
    await using var fixture = new ExecutionFixture(client);
    var session = fixture.Session;
    UserImage image = await SaveImageAsync(session, 8, 6);
    var selection = TestLlmSelection.Default with { SupportsImages = true };
    var input = new UserMessageSubmission(Guid.NewGuid(), 1, "", selection, [image.ImageId]);
    var refused = await session.SubmitUserMessageAsync(input);
    Assert.Equal(MessageRefusalReason.StaleRevision, refused.RefusalReason);
    var retry = await session.SubmitUserMessageAsync(input with { ImageIds = new[] { image.ImageId } });
    Assert.Equal(refused.RefusalReason, retry.RefusalReason);
    Assert.Equal(new[] { image.ImageId }, retry.Submission.ImageIds);
    Assert.Equal(0, client.Calls);
    var resubmitted = await session.SubmitUserMessageAsync(input with { SubmissionId = Guid.NewGuid(), ExpectedHistoryRevision = 0 });
    Assert.Equal(MessageAcceptance.Accepted, resubmitted.Acceptance);
    await session.WaitForExecutionFinishAsync(resubmitted.ExecutionId!.Value).WaitAsync(TestTimeout);
  }

  [Fact]
  public async Task EditsPreserveOmittedImagesClearExplicitEmptyAndFingerprintOrder() {
    using var client = new ScriptedChatClient(Complete);
    await using var fixture = new ExecutionFixture(client);
    var session = fixture.Session;
    UserImage first = await SaveImageAsync(session, 8, 6);
    UserImage second = await SaveImageAsync(session, 9, 7);
    var selection = TestLlmSelection.Default with { SupportsImages = true };
    var accepted = await session.SubmitUserMessageAsync(new(Guid.NewGuid(), 0, "original", selection, [first.ImageId, second.ImageId]));
    await session.WaitForExecutionFinishAsync(accepted.ExecutionId!.Value).WaitAsync(TestTimeout);
    var original = Assert.Single((await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 0))).Entries,
        item => item.Entry.Kind == TranscriptEntryKind.User);
    var preserve = new MessageEditSubmission(Guid.NewGuid(), original.Text!.From.Message, "preserved", selection);
    var preserved = await session.EditUserMessageAsync(preserve);
    await session.WaitForExecutionFinishAsync(preserved.ExecutionId).WaitAsync(TestTimeout);
    var current = Assert.Single((await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 1))).Entries,
        item => item.Entry.Kind == TranscriptEntryKind.User);
    Assert.Equal(new[] { first, second }, current.Images);
    await AssertImageContentsAsync(session, Assert.Single(client.LastRequest, message => message.Role == ChatRole.User), first, second);

    var reorder = new MessageEditSubmission(Guid.NewGuid(), current.Text!.From.Message, "reordered", selection, [second.ImageId, first.ImageId]);
    var reordered = await session.EditUserMessageAsync(reorder);
    await session.WaitForExecutionFinishAsync(reordered.ExecutionId).WaitAsync(TestTimeout);
    Assert.Equal(reordered, await session.EditUserMessageAsync(reorder with { ImageIds = new[] { second.ImageId, first.ImageId } }));
    await Assert.ThrowsAsync<UserMessageConflictException>(() => session.EditUserMessageAsync(
        reorder with { ImageIds = new[] { first.ImageId, second.ImageId } }));
    current = Assert.Single((await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 2))).Entries,
        item => item.Entry.Kind == TranscriptEntryKind.User);
    Assert.Equal(new[] { second, first }, current.Images);
    await AssertImageContentsAsync(session, Assert.Single(client.LastRequest, message => message.Role == ChatRole.User), second, first);

    var clear = new MessageEditSubmission(Guid.NewGuid(), current.Text!.From.Message, "cleared", selection, []);
    var cleared = await session.EditUserMessageAsync(clear);
    await session.WaitForExecutionFinishAsync(cleared.ExecutionId).WaitAsync(TestTimeout);
    current = Assert.Single((await session.Transcript.ReadHistoryAsync(new(TranscriptHistoryMode.Latest, 3))).Entries,
        item => item.Entry.Kind == TranscriptEntryKind.User);
    Assert.True(current.Images is null || current.Images.Count == 0);
    Assert.Empty(Assert.Single(client.LastRequest, message => message.Role == ChatRole.User).Contents.OfType<DataContent>());
    Assert.NotNull(await session.ReadUserImageAsync(first.ImageId));
  }

  private static async Task<UserImage> SaveImageAsync(PrimarySession session, int width, int height) =>
      await session.SaveUserImageAsync(Guid.NewGuid(), ImageBytes(width, height));

  private static byte[] ImageBytes(int width, int height) {
    using var bitmap = new SKBitmap(width, height);
    using var image = SKImage.FromBitmap(bitmap);
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    return encoded.ToArray();
  }

  private static async Task AssertImageContentsAsync(PrimarySession session, ChatMessage message, params UserImage[] images) {
    DataContent[] actual = message.Contents.OfType<DataContent>().ToArray();
    Assert.Equal(images.Length, actual.Length);
    for (int index = 0; index < images.Length; index++) {
      DataContent expected = (await session.ReadUserImageAsync(images[index].ImageId))!;
      Assert.Equal(expected.MediaType, actual[index].MediaType);
      Assert.Equal(expected.Data.ToArray(), actual[index].Data.ToArray());
    }
  }
}
