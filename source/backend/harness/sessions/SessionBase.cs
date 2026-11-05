namespace Urd.Core.Sessions;

public abstract class SessionBase
{
    private protected SessionBase(SessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Id = record.Id;
        HistoryRevision = record.HistoryRevision;
    }

    public Guid Id { get; }

    internal long HistoryRevision { get; }
}
