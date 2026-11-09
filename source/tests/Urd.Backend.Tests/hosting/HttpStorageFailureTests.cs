using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Urd.Backend.Tests.Support;
using Urd.Core.Providers;
using Urd.Core.Storage;
using Urd.Server;
using Xunit;

namespace Urd.Backend.Tests.Hosting;

public sealed class HttpStorageFailureTests {
  [Fact]
  public async Task SqliteFailureIsLoggedAndReturnedAsEmptyUnavailableResponse() =>
      await AssertUnavailableAsync(new SqliteException("private sqlite detail", 1, 1));

  [Fact]
  public async Task InvalidDataFailureIsLoggedAndReturnedAsEmptyUnavailableResponse() =>
      await AssertUnavailableAsync(new InvalidDataException("private data detail"));

  [Fact]
  public async Task ProviderListStorageFailureIsLoggedByItsEndpointFilter() {
    using var database = new TemporaryDatabase();
    using UrdStore store = UrdStore.Open(database.Path);
    var providers = new ProviderManager(store, static () => new Uri("http://localhost/auth/callback"));
    using var logs = new RecordingLoggerProvider();
    try {
      var builder = WebApplication.CreateBuilder();
      builder.Logging.ClearProviders();
      builder.Logging.AddProvider(logs);
      await using WebApplication app = builder.Build();
      app.MapProviders(providers);
      database.Execute("DROP TABLE provider_connections;");

      RouteEndpoint endpoint = Assert.Single(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
          .OfType<RouteEndpoint>(), candidate => candidate.RoutePattern.RawText == "/api/providers/");
      var context = new DefaultHttpContext { RequestServices = app.Services };
      context.Request.Method = HttpMethods.Get;
      context.Request.Path = "/api/providers";
      using var body = new MemoryStream();
      context.Response.Body = body;

      await endpoint.RequestDelegate!(context);

      Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
      Assert.Empty(body.ToArray());
      LogEntry entry = Assert.Single(logs.Entries, candidate => candidate.Level == LogLevel.Error);
      Assert.IsType<SqliteException>(entry.Exception);
      Assert.Contains("provider_connections", entry.Exception.Message, StringComparison.Ordinal);
    } finally {
      await providers.ShutdownAsync().WaitAsync(TestAsync.TestTimeout);
      providers.Dispose();
    }
  }

  private static async Task AssertUnavailableAsync(Exception failure) {
    using var logs = new RecordingLoggerProvider();
    using ILoggerFactory loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logs));
    using ServiceProvider services = new ServiceCollection().AddSingleton(loggerFactory).BuildServiceProvider();
    var context = new DefaultHttpContext { RequestServices = services };
    using var body = new MemoryStream();
    context.Response.Body = body;

    IResult result = HttpStorageFailure.Unavailable(context, failure);
    Assert.Equal(StatusCodes.Status503ServiceUnavailable, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    await result.ExecuteAsync(context);

    Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
    Assert.Empty(body.ToArray());
    LogEntry entry = Assert.Single(logs.Entries, candidate => candidate.Level == LogLevel.Error);
    Assert.Same(failure, entry.Exception);
    Assert.DoesNotContain(failure.Message, entry.Message, StringComparison.Ordinal);
  }

  private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

  private sealed class RecordingLoggerProvider : ILoggerProvider {
    private readonly List<LogEntry> entries = [];

    internal IReadOnlyList<LogEntry> Entries => entries;
    public ILogger CreateLogger(string categoryName) => new RecordingLogger(entries);
    public void Dispose() { }
  }

  private sealed class RecordingLogger(List<LogEntry> entries) : ILogger {
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => entries.Add(new(logLevel, formatter(state, exception), exception));
  }
}
