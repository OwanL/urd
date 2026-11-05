using Microsoft.Extensions.AI;
using Urd.Core.Storage;

namespace Urd.Core.Sessions;

public sealed class PrimarySession : SessionBase
{
    internal PrimarySession(SessionRecord record, SessionStore store, IChatClient chatClient)
        : base(record, store, chatClient)
    {
    }

    /// <summary>Submits incoming user input for a durable acceptance/refusal decision, without awaiting the model answer.</summary>
    public Task<InputReceipt> SubmitUserInputAsync(InputCommand input) => AdmitInputAsync(input);
}
