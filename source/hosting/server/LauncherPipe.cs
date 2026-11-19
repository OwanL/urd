using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Urd.Server;

// The first listener is created synchronously so startup cannot report readiness before it exists.
internal sealed class LauncherPipe : IAsyncDisposable {
  private readonly CancellationTokenSource stopping = new();
  public Task Completion { get; }

  public LauncherPipe(string name, Uri origin) {
    string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The server executable path is unavailable.");
    byte[] response = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
      origin = origin.GetLeftPart(UriPartial.Authority),
      executable
    }) + "\n");
    var listener = CreateListener(name);
    Completion = ServeAsync(name, listener, response, stopping.Token);
  }

  public async ValueTask DisposeAsync() {
    await stopping.CancelAsync();
    try {
      await Completion;
    } finally {
      stopping.Dispose();
    }
  }

  private static NamedPipeServerStream CreateListener(string name) => new(name, PipeDirection.Out, 1,
      PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

  private static async Task ServeAsync(string name, NamedPipeServerStream first, byte[] response,
      CancellationToken stopping) {
    NamedPipeServerStream listener = first;
    try {
      while (true) {
        await using (listener) {
          await listener.WaitForConnectionAsync(stopping);
          try {
            await listener.WriteAsync(response, stopping);
            await listener.FlushAsync(stopping);
          } catch (IOException) {
            // A client may disconnect before reading the response.
          }
        }
        stopping.ThrowIfCancellationRequested();
        listener = CreateListener(name);
      }
    } catch (OperationCanceledException) when (stopping.IsCancellationRequested) {
      await listener.DisposeAsync();
    }
  }
}
