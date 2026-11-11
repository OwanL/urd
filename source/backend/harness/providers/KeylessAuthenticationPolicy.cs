using System.ClientModel.Primitives;

namespace Urd.Core.Providers;

// The SDK requires a policy, not a fake key. This policy deliberately adds no authentication.
internal sealed class KeylessAuthenticationPolicy : AuthenticationPolicy {
  internal static KeylessAuthenticationPolicy Instance { get; } = new();

  public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex) =>
      ProcessNext(message, pipeline, currentIndex);

  public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex) =>
      ProcessNextAsync(message, pipeline, currentIndex);
}
