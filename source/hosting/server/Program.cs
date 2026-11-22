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
using Urd.Core.MarkdownRendering;
using Urd.Core.Providers;
using Urd.Core.Preferences;
using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;
using Urd.Core.Usage;

namespace Urd.Server;

// Owns the local process boundary: database lease, Core composition, HTTP and their ordered shutdown.
internal static class Program {
  public static async Task<int> Main(string[] args) {
    ServerOptions options;
    try {
      options = ServerOptions.Parse(args);
    } catch (ArgumentException failure) {
      await Console.Error.WriteLineAsync(failure.Message);
      await Console.Error.WriteLineAsync("Usage: Urd.Server --data-root <absolute directory> --port <0..65535> [--git-bash <absolute executable>] [--launcher-pipe <name>] [--no-tray]");
      return 2;
    }

    using var stopping = new CancellationTokenSource();
    IDisposable? tray = null;
    StreamWriter? launchLog = null;
    TextWriter originalError = Console.Error;
    try {
      if (options.LauncherPipe is not null) {
        Directory.CreateDirectory(options.DataRoot);
        launchLog = new StreamWriter(new FileStream(Path.Combine(options.DataRoot, "server.log"),
            FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        Console.SetError(launchLog);
      }
      await RunServerAsync(options, (origin, lanHosting) => {
        Console.Out.WriteLine(JsonSerializer.Serialize(new {
          @event = "urd-ready",
          origin = origin.GetLeftPart(UriPartial.Authority)
        }));
        if (options.ShowTray && OperatingSystem.IsWindowsVersionAtLeast(6, 1))
          tray = ServerTray.Show(options.DataRoot, origin, lanHosting, stopping.Cancel);
      }, stopping.Token, warmInstructions: true);
      return 0;
    } catch (Exception failure) {
      await Console.Error.WriteLineAsync(failure.ToString());
      return 1;
    } finally {
      tray?.Dispose();
      if (launchLog is not null) {
        Console.SetError(originalError);
        await launchLog.DisposeAsync();
      }
    }
  }

  internal static Task RunServerAsync(ServerOptions options, Action<Uri> onReady, CancellationToken stopping,
      bool warmInstructions = false) =>
      RunServerAsync(options, (origin, _) => onReady(origin), null, stopping, warmInstructions: warmInstructions);

  internal static Task RunServerAsync(ServerOptions options, Action<Uri, LanHosting> onReady, CancellationToken stopping,
      bool warmInstructions = false) =>
      RunServerAsync(options, onReady, null, stopping, warmInstructions: warmInstructions);

  internal static Task RunServerAsync(ServerOptions options, Action<Uri> onReady, Func<IChatClient>? createClient,
      CancellationToken stopping, Func<UrdStore, Func<Uri>, ProviderManager>? createProviders = null,
      Func<MessageMarkdownService>? createMarkdown = null, bool warmInstructions = false) =>
      RunServerAsync(options, (origin, _) => onReady(origin), createClient, stopping, createProviders, createMarkdown, warmInstructions);

  // Production borrows provider-managed clients. Tests may supply an owned client;
  // the run disposes it only after successful drain, never through a session.
  internal static async Task RunServerAsync(ServerOptions options, Action<Uri, LanHosting> onReady, Func<IChatClient>? createClient,
      CancellationToken stopping, Func<UrdStore, Func<Uri>, ProviderManager>? createProviders = null,
      Func<MessageMarkdownService>? createMarkdown = null, bool warmInstructions = false) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(onReady);
    bool playwrightSupported = PlaywrightConfig.Supported;
    IReadOnlyList<Uri> localBrowserOrigins = playwrightSupported ? PlaywrightConfig.ReadLocalOrigins() : [];
    Directory.CreateDirectory(options.DataRoot);
    // Keep the file, not a stale PID. The OS releases exclusive ownership when this handle/process exits.
    var directoryLock = new FileStream(Path.Combine(options.DataRoot, "urd.lock"),
        FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    var errorLog = new ServerErrorLog(options.DataRoot);
    UrdStore? store = null;
    IChatClient? client = null;
    SessionManager? manager = null;
    ProviderManager? providers = null;
    ModelPriceCatalog? prices = null;
    SessionEventHub? eventHub = null;
    MessageMarkdownService? markdown = null;
    WebApplication? app = null;
    LanHosting? lanHosting = null;
    LauncherPipe? launcherPipe = null;
    Uri? servedOrigin = null;
    ExceptionDispatchInfo? runFailure = null;
    try {
      store = UrdStore.Open(Path.Combine(options.DataRoot, "urd.sqlite"));
      store.RecoverInterruptedExecutions();
      if (createClient is not null)
        client = createClient() ?? throw new InvalidOperationException("The client factory returned no client.");
      Uri CallbackUri() => new(Volatile.Read(ref servedOrigin)
          ?? throw new InvalidOperationException("The server is not ready."), "/auth/callback");
      if (createClient is null && createProviders is null)
        prices = ModelPriceCatalog.Open(options.DataRoot);
      providers = createProviders?.Invoke(store, CallbackUri) ?? new ProviderManager(store, CallbackUri, prices,
          diagnostic => errorLog.Write("provider-failure", diagnostic));
      var preferences = new PreferenceManager(store);
      if (warmInstructions && store.IsSetupComplete() && preferences.Read().WorkingDirectory is { } defaultDirectory)
        store.WarmInstructionResources(defaultDirectory);
      AgentToolSettings? tools = OperatingSystem.IsWindows() || options.GitBashPath is not null
          ? new AgentToolSettings(options.GitBashPath,
              Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                  .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value!, StringComparer.OrdinalIgnoreCase))
          : null;
      manager = new SessionManager(store, client is { } supplied ? _ => supplied : providers.CreateChatClient,
          tools, preferences, new ModelSelector(providers));
      if (playwrightSupported)
        manager.ConfigurePlaywright(options.DataRoot,
            () => Volatile.Read(ref servedOrigin)?.Port ?? 0,
            localBrowserOrigins);
      eventHub = new SessionEventHub(manager);
      // Initial local limits reuse the exercised workload; transient parser memory is separate.
      markdown = createMarkdown?.Invoke() ?? new MessageMarkdownService(new(
          Entries: 64, RetainedBytes: 384L * 1024 * 1024,
          PendingKeys: 32, Leases: 64, SmallMessageUtf8Bytes: 16 * 1024));
      manager.SessionClosed += markdown.CloseSession;
      var lanAccess = new LanAccess();
      app = CreateApplication([IPAddress.Loopback], options.Port, manager, providers, eventHub, markdown, preferences, store,
          () => Volatile.Read(ref servedOrigin), lanAccess, errorLog, lan: false);
      app.MapSessionBrowserSignInRoutes(manager, playwrightSupported);
      app.MapLanHosting(() => lanHosting, () => Volatile.Read(ref servedOrigin));
      var shutdownRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      using var lifetimeStop = app.Lifetime.ApplicationStopping.Register(() => shutdownRequested.TrySetResult());
      using var callerStop = stopping.Register(app.Lifetime.StopApplication);
      await app.StartAsync(stopping);
      IServerAddressesFeature addresses = app.Services.GetRequiredService<IServer>()
          .Features.Get<IServerAddressesFeature>() ?? throw new InvalidOperationException("The server has no bound address.");
      var origin = new Uri(addresses.Addresses.Single());
      lanHosting = new LanHosting(lanAccess, origin.Port, () => LanAccess.AvailableAddresses(), async available => {
        IDisposable? browserPause = null;
        WebApplication? lanApp = null;
        try {
          browserPause = manager.PausePlaywrightForLan();
          lanApp = CreateApplication(available, origin.Port, manager, providers, eventHub, markdown,
              preferences, store, () => Volatile.Read(ref servedOrigin), lanAccess, errorLog, lan: true);
          await lanApp.StartAsync(stopping);
          return new LanListener(lanApp, browserPause);
        } catch {
          try {
            if (lanApp is not null)
              await lanApp.DisposeAsync();
          } finally {
            browserPause?.Dispose();
          }
          throw;
        }
      }, preferences.SetLanHostingEnabled, preferences.Read().LanHostingEnabled);
      if (preferences.Read().LanHostingEnabled)
        await lanHosting.RestoreAsync();
      Volatile.Write(ref servedOrigin, origin);
      if (options.LauncherPipe is not null)
        launcherPipe = new LauncherPipe(options.LauncherPipe, origin);
      onReady(origin, lanHosting);
      if (launcherPipe is null)
        await shutdownRequested.Task;
      else {
        Task completed = await Task.WhenAny(shutdownRequested.Task, launcherPipe.Completion);
        await completed;
      }
    } catch (OperationCanceledException) when (stopping.IsCancellationRequested ||
        app?.Lifetime.ApplicationStopping.IsCancellationRequested == true) {
      // Stopping during startup still requires draining any partially started host below.
    } catch (Exception failure) {
      runFailure = ExceptionDispatchInfo.Capture(failure);
    }

    if (lanHosting is not null) {
      try {
        await lanHosting.DisposeAsync();
      } catch (Exception failure) {
        runFailure = ExceptionDispatchInfo.Capture(runFailure is null ? failure :
            new AggregateException(runFailure.SourceException, failure));
      }
    }
    Volatile.Write(ref servedOrigin, null);
    eventHub?.Dispose();
    Task drained = Task.WhenAll(manager?.ShutdownSessionsAsync(CancellationToken.None) ?? Task.CompletedTask,
        providers?.ShutdownAsync() ?? Task.CompletedTask,
        prices?.DisposeAsync().AsTask() ?? Task.CompletedTask,
        markdown?.DisposeAsync().AsTask() ?? Task.CompletedTask,
        launcherPipe?.DisposeAsync().AsTask() ?? Task.CompletedTask,
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
    providers?.Dispose();
    client?.Dispose();
    store?.Dispose();
    await directoryLock.DisposeAsync();
    runFailure?.Throw();
  }

  private static WebApplication CreateApplication(IReadOnlyList<IPAddress> addresses, int port, SessionManager manager,
      ProviderManager providers, SessionEventHub eventHub, MessageMarkdownService? markdown,
      PreferenceManager preferences, UrdStore store, Func<Uri?> servedOrigin, LanAccess lanAccess,
      ServerErrorLog errorLog, bool lan) {
    // No default appsettings/environment/URL providers: Kestrel configuration can otherwise add listeners.
    WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions {
      ApplicationName = typeof(Program).Assembly.GetName().Name,
      ContentRootPath = AppContext.BaseDirectory,
      EnvironmentName = Environments.Production
    });
    builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel => {
      foreach (IPAddress address in addresses)
        kestrel.Listen(address, port, listener => listener.Protocols = HttpProtocols.Http1);
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
    app.Use(next => new LocalBrowserGate(next, servedOrigin, lanAccess).InvokeAsync);
    if (Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))) {
      app.UseDefaultFiles();
      app.UseStaticFiles(new StaticFileOptions {
        OnPrepareResponse = context => {
          bool asset = context.Context.Request.Path.StartsWithSegments("/assets");
          context.Context.Response.Headers.CacheControl = asset ? "public, max-age=31536000, immutable" : "no-store";
          context.Context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    app.Use(next => new HttpErrorLogging(next, errorLog, lan).InvokeAsync);
        }
      });
    }
    app.MapMethods("/health", [HttpMethods.Get, HttpMethods.Head], (HttpContext context) =>
        context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true
            ? Results.BadRequest() : Results.NoContent());
    app.MapSessions(manager);
    app.MapFiles();
    app.MapEvents(manager, eventHub);
    if (markdown is not null)
      app.MapMarkdown(manager, eventHub, markdown);
    app.MapProviders(providers, preferences);
    app.MapPreferences(preferences, manager);
    app.MapSetup(store);
    app.MapUsage(store);
    return app;
  }

  private sealed class LanListener(WebApplication app, IDisposable? browserPause) : IAsyncDisposable {
    public async ValueTask DisposeAsync() {
      using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
      try {
        await app.StopAsync(timeout.Token);
      } catch (OperationCanceledException) when (timeout.IsCancellationRequested) {
        await Console.Error.WriteLineAsync("LAN hosting stop timed out; closing remaining connections.");
      } finally {
        try {
          await app.DisposeAsync();
        } finally {
          // LAN admission is already closed before the listener is disposed.
          browserPause?.Dispose();
        }
      }
    }
  }
}
