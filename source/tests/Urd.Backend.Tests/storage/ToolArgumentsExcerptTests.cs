using System.Text;
using System.Text.Json;
using Urd.Core.Storage;
using Xunit;

namespace Urd.Backend.Tests.Storage;

public sealed class ToolArgumentsExcerptTests {
  [Fact]
  public void SmallArgumentsRemainSemanticallyUnchanged() {
    const string arguments = """{"name":"read","offset":2,"enabled":true,"nothing":null,"nested":{"ok":true},"values":[1,{"label":"x"}]}""";

    using JsonDocument expected = JsonDocument.Parse(arguments);
    string excerpt = ToolArgumentsExcerpt.Create(arguments);
    using JsonDocument actual = JsonDocument.Parse(excerpt);

    Assert.True(JsonElement.DeepEquals(expected.RootElement, actual.RootElement));
    AssertBounded(excerpt);
  }

  [Fact]
  public void LongTopLevelStringUsesTruncationMarker() {
    string value = new('x', 3000);
    string excerpt = ToolArgumentsExcerpt.Create(JsonSerializer.Serialize(new { value }));
    using JsonDocument actual = JsonDocument.Parse(excerpt);
    JsonElement marker = actual.RootElement.GetProperty("value");

    Assert.Equal(JsonValueKind.Object, marker.ValueKind);
    Assert.Equal(new string('x', 256), marker.GetProperty("$truncated").GetString());
    Assert.Equal(value.Length, marker.GetProperty("length").GetInt32());
    AssertBounded(excerpt);
  }

  [Fact]
  public void ManyScalarPropertiesFitAndCountOmittedMembers() {
    const int propertyCount = 1000;
    string arguments = "{" + string.Join(',', Enumerable.Range(0, propertyCount).Select(index => $"\"p{index}\":{index}")) + "}";
    string excerpt = ToolArgumentsExcerpt.Create(arguments);
    using JsonDocument actual = JsonDocument.Parse(excerpt);
    int excerptPropertyCount = actual.RootElement.EnumerateObject().Count();
    int omitted = actual.RootElement.GetProperty("$omitted").GetInt32();

    Assert.Equal(propertyCount, excerptPropertyCount - 1 + omitted);
    Assert.True(omitted > 0);
    AssertBounded(excerpt);
  }

  [Fact]
  public void KeysOver128Utf8BytesAreOmitted() {
    string key = new('k', 129);
    string arguments = JsonSerializer.Serialize(new Dictionary<string, int> { [key] = 1 });
    string excerpt = ToolArgumentsExcerpt.Create(arguments);
    using JsonDocument actual = JsonDocument.Parse(excerpt);

    Assert.Equal(1, actual.RootElement.GetProperty("$omitted").GetInt32());
    Assert.False(actual.RootElement.TryGetProperty(key, out _));
    AssertBounded(excerpt);
  }

  [Fact]
  public void NestedObjectCountsKeysOver128Utf8BytesAsOmitted() {
    string key = new('k', 129);
    string arguments = JsonSerializer.Serialize(new {
      options = new Dictionary<string, int> { [key] = 1 }
    });
    string excerpt = ToolArgumentsExcerpt.Create(arguments);
    using JsonDocument actual = JsonDocument.Parse(excerpt);
    JsonElement options = actual.RootElement.GetProperty("options");

    Assert.Equal(1, options.GetProperty("$omitted").GetInt32());
    Assert.False(options.TryGetProperty(key, out _));
    AssertBounded(excerpt);
  }

  [Fact]
  public void EscapedSurrogatePairAtPrefixBoundaryIsCutSafely() {
    string arguments = "{\"value\":\"" + new string('a', 254) + "\\\"\\uD83D\\uDE00" + new string('b', 3000) + "\"}";
    string original = new string('a', 254) + "\"😀" + new string('b', 3000);
    string excerpt = ToolArgumentsExcerpt.Create(arguments);
    using JsonDocument actual = JsonDocument.Parse(excerpt);
    JsonElement marker = actual.RootElement.GetProperty("value");
    string prefix = marker.GetProperty("$truncated").GetString()!;

    Assert.Equal(new string('a', 254) + "\"", prefix);
    Assert.Equal(255, prefix.Length);
    Assert.Equal(original.Length, marker.GetProperty("length").GetInt32());
    Assert.Equal(255, Encoding.UTF8.GetByteCount(prefix));
    AssertBounded(excerpt);
  }

  [Fact]
  public void NestedStringsUse256CharacterMarkersAndDropOversizedKeys() {
    string longValue = new('n', 257);
    string longKey = new('k', 129);
    string arguments = JsonSerializer.Serialize(new {
      nested = new Dictionary<string, string> { ["text"] = longValue, [longKey] = "not admitted" }
    });
    string excerpt = ToolArgumentsExcerpt.Create(arguments);
    using JsonDocument actual = JsonDocument.Parse(excerpt);
    JsonElement nested = actual.RootElement.GetProperty("nested");
    JsonElement marker = nested.GetProperty("text");

    Assert.Equal(new string('n', 256), marker.GetProperty("$truncated").GetString());
    Assert.Equal(longValue.Length, marker.GetProperty("length").GetInt32());
    Assert.False(nested.TryGetProperty(longKey, out _));
    AssertBounded(excerpt);
  }

  [Fact]
  public void HugeNestedArrayKeeps16ItemsAndCountsTheRest() {
    const int itemCount = 10000;
    string arguments = JsonSerializer.Serialize(new {
      nested = new { items = Enumerable.Range(0, itemCount).ToArray() }
    });
    string excerpt = ToolArgumentsExcerpt.Create(arguments);
    using JsonDocument actual = JsonDocument.Parse(excerpt);
    JsonElement items = actual.RootElement.GetProperty("nested").GetProperty("items");

    Assert.Equal(17, items.GetArrayLength());
    Assert.Equal(Enumerable.Range(0, 16), items.EnumerateArray().Take(16).Select(item => item.GetInt32()));
    Assert.Equal(itemCount - 16, items[16].GetProperty("$omittedItems").GetInt32());
    AssertBounded(excerpt);
  }

  [Fact]
  public void TwoKiBTopLevelStringBoundaryIsInclusive() {
    string whole = new('x', 2048);
    string wholeExcerpt = ToolArgumentsExcerpt.Create(JsonSerializer.Serialize(new { value = whole }));
    using JsonDocument wholeResult = JsonDocument.Parse(wholeExcerpt);
    Assert.Equal(whole, wholeResult.RootElement.GetProperty("value").GetString());
    AssertBounded(wholeExcerpt);

    string over = new('x', 2049);
    string overExcerpt = ToolArgumentsExcerpt.Create(JsonSerializer.Serialize(new { value = over }));
    using JsonDocument overResult = JsonDocument.Parse(overExcerpt);
    JsonElement marker = overResult.RootElement.GetProperty("value");
    Assert.Equal(new string('x', 256), marker.GetProperty("$truncated").GetString());
    Assert.Equal(over.Length, marker.GetProperty("length").GetInt32());
    AssertBounded(overExcerpt);

    string unicodeBoundary = new('é', 600);
    string unicodeExcerpt = ToolArgumentsExcerpt.Create(JsonSerializer.Serialize(new { value = unicodeBoundary }));
    using JsonDocument unicodeResult = JsonDocument.Parse(unicodeExcerpt);
    Assert.Equal(unicodeBoundary, unicodeResult.RootElement.GetProperty("value").GetString());
    AssertBounded(unicodeExcerpt);

    string unicodeOver = new('é', 1025);
    string unicodeOverExcerpt = ToolArgumentsExcerpt.Create(JsonSerializer.Serialize(new { value = unicodeOver }));
    using JsonDocument unicodeOverResult = JsonDocument.Parse(unicodeOverExcerpt);
    JsonElement unicodeMarker = unicodeOverResult.RootElement.GetProperty("value");
    Assert.Equal(new string('é', 256), unicodeMarker.GetProperty("$truncated").GetString());
    Assert.Equal(unicodeOver.Length, unicodeMarker.GetProperty("length").GetInt32());
    AssertBounded(unicodeOverExcerpt);
  }

  [Fact]
  public void RandomArgumentsAlwaysProduceDeterministicValidBoundedJson() {
    var random = new Random(8675309);
    for (int iteration = 0; iteration < 24; iteration++) {
      var arguments = Enumerable.Range(0, random.Next(20, 80))
          .ToDictionary(index => index % 3 == 0 ? $"$field{index}" : $"field{index}", _ => RandomValue(random));
      string json = JsonSerializer.Serialize(arguments);
      string excerpt = ToolArgumentsExcerpt.Create(json);

      Assert.Equal(excerpt, ToolArgumentsExcerpt.Create(json));
      AssertBounded(excerpt);
    }
  }

  private static object? RandomValue(Random random) => random.Next(6) switch {
    0 => new string((char)random.Next(32, 127), random.Next(0, 1800)),
    1 => random.NextInt64(),
    2 => random.Next(2) == 0,
    3 => null,
    4 => Enumerable.Range(0, random.Next(0, 30)).Select(_ => random.Next(1000)).ToArray(),
    _ => new Dictionary<string, object?> {
      ["text"] = new string('q', random.Next(0, 400)),
      ["items"] = Enumerable.Range(0, random.Next(0, 30)).Select(_ => random.Next(1000)).ToArray()
    }
  };

  private static void AssertBounded(string excerpt) {
    Assert.True(Encoding.UTF8.GetByteCount(excerpt) <= 4096);
    using JsonDocument parsed = JsonDocument.Parse(excerpt);
    Assert.Equal(JsonValueKind.Object, parsed.RootElement.ValueKind);
  }
}
