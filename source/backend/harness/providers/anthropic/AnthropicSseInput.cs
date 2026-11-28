namespace Urd.Core.Providers;

// The framework parser can retain arbitrarily long incomplete events. Bound input between
// dispatched events, with at most one small read of lookahead from the previous event.
internal sealed class AnthropicSseInput(Stream source) : Stream {
  internal const int MaximumEventBytes = 2 * LlmProviderContext.MaximumBytes;
  private const int MaximumReadBytes = 16 * 1024;
  private int remaining = MaximumEventBytes;

  internal void BeginEvent() => remaining = MaximumEventBytes;

  public override bool CanRead => true;
  public override bool CanSeek => false;
  public override bool CanWrite => false;
  public override long Length => throw new NotSupportedException();
  public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
  public override void Flush() => throw new NotSupportedException();
  public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
  public override void SetLength(long value) => throw new NotSupportedException();
  public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

  public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
    int count = await source.ReadAsync(buffer[..Math.Min(buffer.Length, Math.Min(remaining + 1, MaximumReadBytes))],
        cancellationToken).ConfigureAwait(false);
    remaining -= count;
    if (remaining < 0) throw AnthropicJson.Invalid("Anthropic SSE event exceeds the size limit");
    return count;
  }
}
