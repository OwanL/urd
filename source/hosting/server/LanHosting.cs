using System.Net;

namespace Urd.Server;

// Serializes listener changes and publishes admission only after all addresses bind.
internal sealed class LanHosting : IAsyncDisposable {
  private readonly LanAccess access;
  private readonly Func<IReadOnlyList<IPAddress>> addresses;
  private readonly Func<IReadOnlyList<IPAddress>, Task<IAsyncDisposable>> start;
  private readonly Action<bool> persist;
  private readonly SemaphoreSlim mutex = new(1, 1);
  private IAsyncDisposable? listener;
  private bool disposed;
  private bool remembered;
  private string? startupError;
  private readonly int boundPort;

  internal LanHosting(LanAccess access, int port, Func<IReadOnlyList<IPAddress>> addresses,
      Func<IReadOnlyList<IPAddress>, Task<IAsyncDisposable>> start, Action<bool> persist, bool remembered = false) {
    this.access = access;
    this.addresses = addresses;
    this.start = start;
    this.persist = persist;
    this.remembered = remembered;
    boundPort = port;
  }

  public bool Enabled => access.Snapshot.Enabled;
  public IReadOnlyList<string> Origins => access.Snapshot.Origins(boundPort);

  private LanHostingStatus Snapshot() {
    LanAccessSnapshot current = access.Snapshot;
    return new(current.Enabled, remembered, current.Origins(boundPort), startupError);
  }

  public async Task<LanHostingStatus> StatusAsync() {
    await mutex.WaitAsync();
    try {
      return Snapshot();
    } finally {
      mutex.Release();
    }
  }

  public async Task RestoreAsync() {
    try {
      await SetEnabledAsync(true);
    } catch (Exception failure) {
      await mutex.WaitAsync();
      try {
        startupError = "Could not start LAN hosting. Retry or disable it.";
      } finally {
        mutex.Release();
      }
      await Console.Error.WriteLineAsync($"Could not restore LAN hosting: {failure}");
    }
  }

  public async Task SetEnabledAsync(bool enabled) => await ChangeAsync(enabled);

  public async Task<LanHostingStatus> ChangeAsync(bool enabled) {
    await mutex.WaitAsync();
    try {
      ObjectDisposedException.ThrowIf(disposed, this);
      if (enabled) {
        if (listener is not null)
          return Snapshot();
        IReadOnlyList<IPAddress> available = addresses();
        if (available.Count == 0)
          throw new InvalidOperationException("No private IPv4 addresses are available for LAN hosting.");
        IAsyncDisposable started = await start(available);
        try {
          access.Replace(available);
          persist(true);
          remembered = true;
          startupError = null;
          listener = started;
        } catch {
          access.Disable();
          await started.DisposeAsync();
          throw;
        }
      } else {
        access.Disable();
        IAsyncDisposable? previous = listener;
        listener = null;
        try {
          persist(false);
          remembered = false;
          startupError = null;
        } finally {
          if (previous is not null)
            await previous.DisposeAsync();
        }
      }
      return Snapshot();
    } finally {
      mutex.Release();
    }
  }

  public async ValueTask DisposeAsync() {
    await mutex.WaitAsync();
    try {
      if (disposed)
        return;
      disposed = true;
      access.Disable();
      IAsyncDisposable? previous = listener;
      listener = null;
      if (previous is not null)
        await previous.DisposeAsync();
    } finally {
      mutex.Release();
    }
  }
}
