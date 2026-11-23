namespace Urd.Core.Sessions;

// The in-memory question broker owned by SessionManager. Each recipient has one FIFO mailbox
// whose head is the only current question. The recipient's session presents the head at a
// request claim and records which requests saw it, so an answer completes only the question its
// own request was shown. Records settle exactly once — answered, cancelled by the waiting ask, or
// failed — and completions are signalled outside the lock. No SQLite, wake or join runs here.
internal sealed class AgentQuestions {
  internal const string RecipientFinished = "The recipient agent finished without answering the question.";
  private const string RecipientFailedBeforeDelivery = "Your question was not delivered because the recipient's run failed before it saw it. You can ask again if the recipient is still available.";

  private readonly object stateLock = new();
  private readonly Dictionary<Guid, Mailbox> mailboxes = [];
  // Child -> owning call. Background dependencies are active only while their own execution parks.
  private readonly Dictionary<Guid, AgentWaitSource> delegations = [];
  private readonly Dictionary<Guid, Guid> parked = [];
  private bool closed;

  private const string SelfAsk = "An agent cannot ask itself a question.";
  private const string ForegroundAncestor = "The ancestor agent is waiting for a foreground delegation to finish. Interactive delegation requires a background child.";
  private const string WaitCycle = "The question would create a wait cycle: the recipient is already waiting on the asking agent, directly or indirectly.";

  internal Task<AgentQuestionResult> Enqueue(Guid recipientId, Guid senderId, Guid requestId, string callId,
      string prompt, bool wake, CancellationToken cancellation) =>
      Enqueue(recipientId, new(senderId, Guid.Empty, Background: !wake), requestId, callId, prompt, wake,
          cancellation, out _);

  // Enqueues one question in FIFO order and returns its settling task. Admission checks the
  // wait graph in the same transition that makes the question visible to other calls.
  internal Task<AgentQuestionResult> Enqueue(Guid recipientId, AgentWaitSource source, Guid requestId, string callId,
      string prompt, bool wake, CancellationToken cancellation, out Guid questionId) {
    ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
    var record = new Question(recipientId, source, requestId, callId, prompt, wake);
    questionId = record.QuestionId;
    lock (stateLock) {
      Mailbox? mailbox = closed ? null : GetOrCreate(recipientId);
      if (mailbox is null || mailbox.Closed) {
        record.Completion.TrySetResult(new(null, RecipientFinished));
        return record.Completion.Task;
      }

      if (cancellation.IsCancellationRequested) {
        record.Completion.TrySetCanceled(cancellation);
        return record.Completion.Task;
      }

      string? rejection = RejectAsk(source, recipientId);
      if (rejection is not null) {
        record.Completion.TrySetResult(new(null, rejection));
        return record.Completion.Task;
      }

      mailbox.Questions.Add(record);
    }

    // Cancellation or shutdown may settle the record before registration is installed.
    var registration = cancellation.Register(() => Cancel(recipientId, record.QuestionId, cancellation));
    lock (stateLock) {
      if (!record.Settled) {
        record.Registration = registration;
        return record.Completion.Task;
      }
    }
    registration.Unregister();
    return record.Completion.Task;
  }

  // Install ownership before the child can run or be addressed. A delegation is never the
  // victim of a cycle; fail the implicated ask(s) if a delegation edge closes it instead.
  internal void TrackDelegation(Guid childId, AgentWaitSource owner) {
    List<Question> rejected = [];
    lock (stateLock) {
      if (closed)
        return;
      delegations.Add(childId, owner);
      if (IsBlocking(owner))
        BreakDelegationCycles(owner.SessionId, childId, rejected);
    }
    SettleCycles(rejected);
  }

  // Install the real driver wait, not a latched UI status. An ask may have been queued before
  // this transition, or arrive later while this execution is already parked.
  internal void Park(Guid sessionId, Guid executionId) {
    List<Question> rejected = [];
    lock (stateLock) {
      if (closed)
        return;
      parked.Add(sessionId, executionId);
      var source = new AgentWaitSource(sessionId, executionId, Background: true);
      foreach (var (childId, owner) in delegations) {
        if (owner == source)
          BreakDelegationCycles(sessionId, childId, rejected);
      }
      foreach (Mailbox mailbox in mailboxes.Values) {
        for (int index = 0; index < mailbox.Questions.Count;) {
          Question question = mailbox.Questions[index];
          if (question.Source == source && TryFindCycle(sessionId, question.RecipientId, out _))
            rejected.Add(Detach(mailbox, question));
          else
            index++;
        }
      }
    }
    SettleCycles(rejected);
  }

  internal void Unpark(Guid sessionId, Guid executionId) {
    lock (stateLock) {
      if (parked.TryGetValue(sessionId, out Guid current) && current == executionId)
        parked.Remove(sessionId);
    }
  }

  // All graph queries below run under stateLock. Walk ownership independently of the wait
  // graph: a descendant cannot reach a waiting foreground ancestor by backgrounding its ask
  // or by being delegated in the background at an intermediate level.
  private string? RejectAsk(AgentWaitSource source, Guid recipientId) {
    if (source.SessionId == recipientId)
      return SelfAsk;
    for (Guid childId = source.SessionId; delegations.TryGetValue(childId, out AgentWaitSource owner);
        childId = owner.SessionId) {
      if (owner.SessionId == recipientId && !owner.Background)
        return ForegroundAncestor;
    }
    return IsBlocking(source) && TryFindCycle(source.SessionId, recipientId, out _) ? WaitCycle : null;
  }

  private bool IsBlocking(AgentWaitSource source) => !source.Background
      || (parked.TryGetValue(source.SessionId, out Guid executionId) && executionId == source.ExecutionId);

  private void BreakDelegationCycles(Guid senderId, Guid recipientId, List<Question> rejected) {
    // Delegations alone form a tree, so every return path must contain a question.
    while (TryFindCycle(senderId, recipientId, out Question? ask) && ask is not null)
      rejected.Add(Detach(mailboxes[ask.RecipientId], ask));
  }

  private static void SettleCycles(List<Question> rejected) {
    foreach (Question ask in rejected)
      Settle(ask, new(null, WaitCycle));
  }

  // To check sender -> recipient, walk the incoming edges from sender back to recipient.
  // Mailboxes and ownership are already indexed by destination; no duplicate graph is needed.
  private bool TryFindCycle(Guid senderId, Guid recipientId, out Question? implicatedAsk) {
    var visited = new HashSet<Guid>();
    var pending = new Stack<(Guid SessionId, Question? Ask)>();
    pending.Push((senderId, null));
    while (pending.TryPop(out var step)) {
      if (!visited.Add(step.SessionId))
        continue;
      if (step.SessionId == recipientId) {
        implicatedAsk = step.Ask;
        return true;
      }
      if (delegations.TryGetValue(step.SessionId, out AgentWaitSource owner) && IsBlocking(owner))
        pending.Push((owner.SessionId, step.Ask));
      if (mailboxes.TryGetValue(step.SessionId, out Mailbox? mailbox)) {
        foreach (Question question in mailbox.Questions.Where(question => IsBlocking(question.Source)))
          pending.Push((question.SenderId, question));
      }
    }
    implicatedAsk = null;
    return false;
  }

  // Cancels one waiting record exactly once; absent or already settled records stay untouched.
  private void Cancel(Guid recipientId, Guid questionId, CancellationToken cancellation) {
    Question? question = Remove(recipientId, questionId);
    if (question is null)
      return;

    question.Registration.Unregister();
    question.Completion.TrySetCanceled(cancellation);
  }

  // Answers the head only when the invoking request was shown it. A duplicate or late answer
  // therefore never consumes the successor, which no earlier request has seen.
  internal bool AnswerPresented(Guid recipientId, Guid requestId, string answer) {
    ArgumentException.ThrowIfNullOrWhiteSpace(answer);
    Question head;
    lock (stateLock) {
      if (!mailboxes.TryGetValue(recipientId, out Mailbox? mailbox) || PresentedHead(mailbox, requestId) is not { } presented)
        return false;

      head = Detach(mailbox, presented);
    }

    Settle(head, new(answer, null));
    return true;
  }

  // A compaction reminder can only describe the same live head this request was bound to.
  internal (Guid QuestionId, Guid SenderId, string Prompt)? ReadPresented(Guid recipientId, Guid requestId) {
    lock (stateLock)
      return mailboxes.TryGetValue(recipientId, out Mailbox? mailbox) && PresentedHead(mailbox, requestId) is { } head
          ? (head.QuestionId, head.SenderId, head.Prompt) : null;
  }

  // Caller holds stateLock.
  private static Question? PresentedHead(Mailbox mailbox, Guid requestId) =>
      mailbox.Questions.Count > 0 && mailbox.Questions[0].PresentedRequests?.Contains(requestId) == true
          ? mailbox.Questions[0] : null;

  // Captures the head for one recipient request claim. The first capture is the presentation the
  // caller writes to history; later captures let following requests answer the same question.
  // The caller holds the recipient's session lock and withdraws the capture if no request is made.
  internal QuestionPresentation? Present(Guid recipientId, Guid requestId) {
    lock (stateLock) {
      if (!mailboxes.TryGetValue(recipientId, out Mailbox? mailbox) || mailbox.Questions.Count == 0)
        return null;

      Question head = mailbox.Questions[0];
      bool first = head.PresentedRequests is null;
      (head.PresentedRequests ??= []).Add(requestId);
      return new(head.QuestionId, head.SenderId, head.Prompt, first,
          first && mailbox.Questions.Exists(question => question.Wake));
    }
  }

  // Reverses one capture whose request was not created; a first presentation becomes unpresented.
  internal void Withdraw(Guid recipientId, QuestionPresentation presentation, Guid requestId) {
    lock (stateLock) {
      Question? question = mailboxes.TryGetValue(recipientId, out Mailbox? mailbox)
          ? mailbox.Questions.Find(candidate => candidate.QuestionId == presentation.QuestionId) : null;
      if (question is null)
        return;

      question.PresentedRequests?.Remove(requestId);
      if (presentation.First)
        question.PresentedRequests = null;
    }
  }

  // Fails only the presented head when the recipient's execution ends, stops or faults
  // without answering.
  internal void FailPresented(Guid recipientId) {
    Question head;
    lock (stateLock) {
      if (!mailboxes.TryGetValue(recipientId, out Mailbox? mailbox) || mailbox.Questions.Count == 0
          || mailbox.Questions[0].PresentedRequests is null)
        return;

      head = Detach(mailbox, mailbox.Questions[0]);
    }

    Settle(head, new(null, RecipientFinished));
  }

  // An unsuccessful run will not wake itself to deliver these asks. Leave opportunistic
  // questions queued in FIFO order and release every affected waiter outside the lock.
  internal void FailUndeliveredWaking(Guid recipientId) {
    List<Question> failed = [];
    lock (stateLock) {
      if (!mailboxes.TryGetValue(recipientId, out Mailbox? mailbox))
        return;

      mailbox.Questions.RemoveAll(question => {
        if (!question.Wake || question.PresentedRequests is not null)
          return false;
        question.Settled = true;
        failed.Add(question);
        return true;
      });
    }

    foreach (Question question in failed)
      Settle(question, new(null, RecipientFailedBeforeDelivery));
  }

  // Fails one waiting record with a stated reason; absent or settled records stay untouched.
  internal void Fail(Guid recipientId, Guid questionId, string error) {
    Question? question = Remove(recipientId, questionId);
    if (question is not null)
      Settle(question, new(null, error));
  }

  // Closes one recipient's mailbox: further asks fail immediately and every remaining record
  // fails as unanswered. Completions are signalled after the lock releases.
  internal void CloseRecipient(Guid recipientId) => CloseRecipient(recipientId, forget: false);

  // Closed primaries have no future admission path, so they don't need mailbox tombstones.
  internal void ForgetRecipient(Guid recipientId) => CloseRecipient(recipientId, forget: true);

  private void CloseRecipient(Guid recipientId, bool forget) {
    Question[] unsettled;
    lock (stateLock) {
      if (closed)
        return;
      delegations.Remove(recipientId);
      Mailbox? mailbox;
      if (forget) {
        if (!mailboxes.Remove(recipientId, out mailbox))
          return;
      } else {
        mailbox = GetOrCreate(recipientId);
        mailbox.Closed = true;
      }
      unsettled = [.. mailbox.Questions];
      foreach (Question question in unsettled)
        question.Settled = true;
      mailbox.Questions.Clear();
    }

    foreach (Question question in unsettled)
      Settle(question, new(null, RecipientFinished));
  }

  // Closes every mailbox when the whole manager stops; no ask survives a restart.
  internal void CloseAll() {
    Question[] unsettled;
    lock (stateLock) {
      closed = true;
      unsettled = [.. mailboxes.Values.SelectMany(mailbox => mailbox.Questions)];
      foreach (Question question in unsettled)
        question.Settled = true;
      mailboxes.Clear();
      delegations.Clear();
      parked.Clear();
    }

    foreach (Question question in unsettled)
      Settle(question, new(null, RecipientFinished));
  }

  // Bounded observation of one recipient's unanswered questions, head first.
  internal IReadOnlyList<QueuedQuestion> Queued(Guid recipientId) {
    lock (stateLock)
      return mailboxes.TryGetValue(recipientId, out Mailbox? mailbox)
          ? mailbox.Questions.Select(question => question.Observe()).ToArray()
          : [];
  }

  private Question? Remove(Guid recipientId, Guid questionId) {
    lock (stateLock) {
      if (!mailboxes.TryGetValue(recipientId, out Mailbox? mailbox))
        return null;

      int index = mailbox.Questions.FindIndex(question => question.QuestionId == questionId);
      return index < 0 ? null : Detach(mailbox, mailbox.Questions[index]);
    }
  }

  // Unregister without waiting for a cancellation callback that may already be running.
  private static void Settle(Question question, AgentQuestionResult result) {
    question.Registration.Unregister();
    question.Completion.TrySetResult(result);
  }

  private static Question Detach(Mailbox mailbox, Question question) {
    question.Settled = true;
    mailbox.Questions.Remove(question);
    return question;
  }

  private Mailbox GetOrCreate(Guid recipientId) {
    if (!mailboxes.TryGetValue(recipientId, out Mailbox? mailbox)) {
      mailbox = new Mailbox();
      mailboxes[recipientId] = mailbox;
    }

    return mailbox;
  }

  // One waiting ask with its pinned sender, recipient and calling identity. The broker owns
  // settlement, cancellation and cleanup for exactly one terminal transition per record.
  private sealed class Question(Guid recipientId, AgentWaitSource source, Guid requestId, string callId, string prompt, bool wake) {
    public Guid QuestionId { get; } = Guid.NewGuid();
    public Guid RecipientId { get; } = recipientId;
    public AgentWaitSource Source { get; } = source;
    public Guid SenderId => Source.SessionId;
    public Guid RequestId { get; } = requestId;
    public string CallId { get; } = callId;
    public string Prompt { get; } = prompt;
    public bool Wake { get; } = wake;
    public bool Settled { get; set; }
    // Recipient requests shown this question; null until its first presentation.
    public HashSet<Guid>? PresentedRequests { get; set; }
    public CancellationTokenRegistration Registration { get; set; }
    public TaskCompletionSource<AgentQuestionResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public QueuedQuestion Observe() =>
        new(QuestionId, SenderId, RecipientId, RequestId, CallId, Prompt, Wake, PresentedRequests is not null);
  }

  private sealed class Mailbox {
    public bool Closed { get; set; }
    public List<Question> Questions { get; } = []; // Head first.
  }
}

// Ownership and effective mode of an ask or a delegated child.
internal readonly record struct AgentWaitSource(Guid SessionId, Guid ExecutionId, bool Background);

// One settled ask: the answer text, or the stated reason no answer arrived.
internal sealed record AgentQuestionResult(string? Answer, string? Error);

// A bounded observation of one unanswered ask: sender, pinned recipient and call identity.
internal sealed record QueuedQuestion(Guid QuestionId, Guid SenderId, Guid RecipientId, Guid RequestId,
    string CallId, string Prompt, bool Wake, bool Presented);

// The head captured for one recipient request. First marks the presentation written to history;
// Wakes lets that presentation create a request on its own instead of riding along with other work.
internal sealed record QuestionPresentation(Guid QuestionId, Guid SenderId, string Prompt, bool First, bool Wakes);
