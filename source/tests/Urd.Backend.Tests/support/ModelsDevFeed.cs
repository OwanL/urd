using System.Net;
using Urd.Core.Usage;
using Xunit;

namespace Urd.Backend.Tests.Support;

internal sealed class ModelsDevFeed(Func<int, CancellationToken, Task<string>> respond) : HttpMessageHandler {
  private int calls;
  internal int Calls => Volatile.Read(ref calls);

  protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
    Assert.Equal(HttpMethod.Get, request.Method);
    Assert.Equal(ModelsDevPriceSource.Feed, request.RequestUri);
    Assert.Null(request.Headers.Authorization);
    string body = await respond(Interlocked.Increment(ref calls), cancellationToken);
    return new(HttpStatusCode.OK) { Content = new StringContent(body) };
  }

  internal const string ChatCatalog = """
    {
      "openai":{"models":{
        "gpt-6.2-sol":{"id":"gpt-6.2-sol","canonical_id":"openai/gpt-6.2-sol","type":"chat","name":"GPT-6.2 Sol",
          "cost":{"text":{"input":{"price":7},"output":{"price":14}}}}
      }},
      "github-copilot":{"models":{
        "gpt-6.2-sol":{"id":"gpt-6.2-sol","canonical_id":"openai/gpt-6.2-sol","type":"chat","name":"GPT-6.2 Sol",
          "cost":{"text":{"input":{"price":8},"output":{"price":16}}}}
      }},
      "ollama-cloud":{"models":{
        "fixture-chat":{"id":"fixture-chat","canonical_id":"fixture/chat-model","type":"chat","name":"Fixture Chat",
          "cost":{"text":{"input":{"price":0.1},"output":{"price":0.2}},"multipliers":[{"when":{"utc_hours":{"gte":"18:00"}},"factor":2}]}},
        "fixed-chat":{"id":"fixed-chat","canonical_id":"fixture/fixed-chat","type":"chat","name":"Fixed Chat",
          "cost":{"text":{"input":{"price":1},"output":{"price":2}}}}
      }},
      "openrouter":{"models":{
        "fixture/chat-model":{"id":"fixture/chat-model","canonical_id":"fixture/chat-model","type":"chat","name":"Fixture Chat",
          "cost":{"text":{"input":{"price":3},"output":{"price":5}}}}
      }}
    }
    """;
}
