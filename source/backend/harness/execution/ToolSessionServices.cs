using Urd.Core.Sessions;
using Urd.Core.Storage;
using Urd.Core.Tools;

namespace Urd.Core.Execution;

// Restricts harness tool session access to identity lookup.
internal sealed class ToolSessionServices(SessionStorage sessions) : IHarnessSessionServices {
  public SessionRecord? Read(Guid sessionId) => sessions.Read(sessionId);
}
