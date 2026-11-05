using Microsoft.Extensions.AI;
using Urd.Core.Storage;

namespace Urd.Core.Sessions;

public sealed class PrimarySession : SessionBase
{
    internal PrimarySession(SessionRecord record, SessionStore store, IChatClient chatClient)
        : base(record, store, chatClient)
    {
    }

    public Task<InputReceipt> SubmitAsync(InputCommand command) => AdmitInputAsync(command);
}
