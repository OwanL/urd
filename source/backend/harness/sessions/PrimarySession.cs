using Microsoft.Extensions.AI;
using Urd.Core.Storage;

namespace Urd.Core.Sessions;

public sealed class PrimarySession : SessionBase
{
    internal PrimarySession(SessionRecord record, SessionStore store, IChatClient chatClient)
        : base(record, store, chatClient)
    {
    }

    // Returns a saved acceptance/refusal decision, without waiting for the model answer.
    public Task<InputReceipt> SubmitUserInputAsync(InputCommand input) => AdmitInputAsync(input);
}
