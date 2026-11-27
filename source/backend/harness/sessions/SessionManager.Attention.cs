namespace Urd.Core.Sessions;

public sealed partial class SessionManager {
#pragma warning disable S3264 // Each subscriber is invoked separately so a failure cannot block the others.
  internal event Action<AttentionKind>? Attention;
#pragma warning restore S3264

  internal void NotifyAttention(AttentionKind kind) {
    if (Attention is not { } observers)
      return;

    foreach (Action<AttentionKind> observer in observers.GetInvocationList().Cast<Action<AttentionKind>>()) {
      try {
        observer(kind);
      } catch {
        // Attention is best effort and must not disrupt session work or other observers.
      }
    }
  }
}
