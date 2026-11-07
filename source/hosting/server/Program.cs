using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Console;
using Urd.Core.Sessions;
using Urd.Core.Storage;

namespace Urd.Server;

// Owns the local process boundary: database lease, Core composition, HTTP and their ordered shutdown.
internal static class Program {
  public static async Task<int> Main(string[] args) {
    ServerOptions options;
    try {
      options = ServerOptions.Parse(args);
    } catch (ArgumentException failure) {
      await Console.Error.WriteLineAsync(failure.Message);
      await Console.Error.WriteLineAsync("Usage: Urd.Server --data-root <absolute directory> --port <0..65535>");
      return 2;
    }

    try {
      await RunServerAsync(options, origin => Console.Out.WriteLine(JsonSerializer.Serialize(new {
        @event = "urd-ready",
        origin = origin.GetLeftPart(UriPartial.Authority)
      })), CancellationToken.None);
      return 0;
    } catch (Exception failure) {
      await Console.Error.WriteLineAsync(failure.ToString());
      return 1;
    }
  }

  internal static Task RunServerAsync(ServerOptions options, Action<Uri> onReady, CancellationToken stopping) =>
      RunServerAsync(options, onReady, static () => new FakeChatClient(), stopping);

  // The run constructs and owns the supplied client, including its disposal after successful drain.
  internal static async Task RunServerAsync(ServerOptions options, Action<Uri> onReady, Func<IChatClient> createClient,
      CancellationToken stopping) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(onReady);
    ArgumentNullException.ThrowIfNull(createClient);
    Directory.CreateDirectory(options.DataRoot);
    // Keep the file, not a stale PID. The OS releases exclusive ownership when this handle/process exits.
    var directoryLock = new FileStream(Path.Combine(options.DataRoot, "urd.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    SessionStore? store = null;
    IChatClient? client = null;
    SessionManager? manager = null;
    WebApplication? app = null;
    Uri? servedOrigin = null;
    ExceptionDispatchInfo? runFailure = null;
    try {
      store = SessionStore.Open(Path.Combine(options.DataRoot, "urd.sqlite"));
      client = createClient();
      manager = new SessionManager(store, client);
      app = CreateApplication(options, manager, () => Volatile.Read(ref servedOrigin));
      var shutdownRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      using var lifetimeStop = app.Lifetime.ApplicationStopping.Register(() => shutdownRequested.TrySetResult());
      using var callerStop = stopping.Register(app.Lifetime.StopApplication);
      await app.StartAsync(stopping);
      IServerAddressesFeature addresses = app.Services.GetRequiredService<IServer>()
          .Features.Get<IServerAddressesFeature>() ?? throw new InvalidOperationException("The server has no bound address.");
      var origin = new Uri(addresses.Addresses.Single());
      Volatile.Write(ref servedOrigin, origin);
      onReady(origin);
      await shutdownRequested.Task;
    } catch (OperationCanceledException) when (stopping.IsCancellationRequested ||
        app?.Lifetime.ApplicationStopping.IsCancellationRequested == true) {
      // Stopping during startup still requires draining any partially started host below.
    } catch (Exception failure) {
      runFailure = ExceptionDispatchInfo.Capture(failure);
    }

    Volatile.Write(ref servedOrigin, null);
    Task drained = Task.WhenAll(manager?.ShutdownSessionsAsync(CancellationToken.None) ?? Task.CompletedTask,
        app?.StopAsync(CancellationToken.None) ?? Task.CompletedTask);
    try {
      await drained;
    } catch (Exception failure) {
      Exception[] failures = runFailure is null ? [drained.Exception ?? failure]
          : [runFailure.SourceException, drained.Exception ?? failure];
      // Failed waits are not a disposal barrier. Main reports failure and exits, without forcing cleanup.
      throw new AggregateException("Server shutdown failed; owned resources were not disposed.", failures);
    }

    if (app is not null)
      await app.DisposeAsync();
    client?.Dispose();
    store?.Dispose();
    await directoryLock.DisposeAsync();
    runFailure?.Throw();
  }

  private static WebApplication CreateApplication(ServerOptions options, SessionManager manager,
      Func<Uri?> servedOrigin) {
    // No default appsettings/environment/URL providers: Kestrel configuration can otherwise add listeners.
    WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions {
      ApplicationName = typeof(Program).Assembly.GetName().Name,
      ContentRootPath = AppContext.BaseDirectory,
      EnvironmentName = Environments.Production
    });
    builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel => {
      kestrel.Listen(IPAddress.Loopback, options.Port, listener => listener.Protocols = HttpProtocols.Http1);
      kestrel.Limits.MaxRequestBodySize = 128 * 1024;
    });
    builder.Host.UseConsoleLifetime(lifetime => lifetime.SuppressStatusMessages = true);
    builder.Services.AddRouting();
    builder.Services.ConfigureHttpJsonOptions(json => {
      json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
      json.SerializerOptions.RespectRequiredConstructorParameters = true;
      json.SerializerOptions.RespectNullableAnnotations = true;
    });
    builder.Services.Configure<HostOptions>(host => host.ShutdownTimeout = Timeout.InfiniteTimeSpan);
    builder.Logging.AddSimpleConsole(console => console.SingleLine = true);
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Services.Configure<ConsoleLoggerOptions>(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
    WebApplication app = builder.Build();
    app.Use(next => new LocalBrowserGate(next, servedOrigin).InvokeAsync);
    app.MapMethods("/health", [HttpMethods.Get, HttpMethods.Head], (HttpContext context) =>
        context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
            ? Results.BadRequest() : Results.NoContent());
    app.MapSessions(manager);
    return app;
  }
}
