namespace Urd.Core.Sessions;

// The in-memory question broker owned by SessionManager. Each recipient has one FIFO mailbox
// whose head is the only current question. The recipient's session presents the head at a
// request claim and records which requests saw it, so an answer completes only the question its
// own request was shown. Records settle exactly once — answered, cancelled by the waiting ask, or
// failed — and completions are signalled outside the lock. No SQLite, wake or join runs here.
internal sealed class AgentQuestions {
  // Every failed record carries the same blame: the recipient ended without answering.
  internal const string RecipientFinished = "The recipient agent finished without answering the question.";

  private readonly object stateLock = new();
  private readonly Dictionary<Guid, Mailbox> mailboxes = [];
  private bool closed;

  internal Task<AgentQuestionResult> Enqueue(Guid recipientId, Guid senderId, Guid requestId, string callId,
      string prompt, bool wake, CancellationToken cancellation) =>
      Enqueue(recipientId, senderId, requestId, callId, prompt, wake, cancellation, out _);

  // Enqueues one question in FIFO order and returns its settling task. The ask's cancellation
  // removes its own record whenever it has not already settled; a closed mailbox returns the
  // task already failed, so the ask never waits on a recipient that cannot answer.
  internal Task<AgentQuestionResult> Enqueue(Guid recipientId, Guid senderId, Guid requestId, string callId,
      string prompt, bool wake, CancellationToken cancellation, out Guid questionId) {
    ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
    var record = new Question(recipientId, senderId, requestId, callId, prompt, wake);
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
      if (!mailboxes.TryGetValue(recipientId, out Mailbox? mailbox) || mailbox.Questions.Count == 0
          || mailbox.Questions[0].PresentedRequests?.Contains(requestId) != true)
        return false;

      head = Detach(mailbox, mailbox.Questions[0]);
    }

    Settle(head, new(answer, null));
    return true;
  }

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

  // Fails the presented head when the recipient's execution ends, stops or faults without
  // answering. Unpresented questions stay queued for the recipient's next run.
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

  // Fails one waiting record with a stated reason; absent or settled records stay untouched.
  internal void Fail(Guid recipientId, Guid questionId, string error) {
    Question? question = Remove(recipientId, questionId);
    if (question is not null)
      Settle(question, new(null, error));
  }

  // Closes one recipient's mailbox: further asks fail immediately and every remaining record
  // fails as unanswered. Completions are signalled after the lock releases.
  internal void CloseRecipient(Guid recipientId) {
    Question[] unsettled;
    lock (stateLock) {
      if (closed)
        return;
      Mailbox mailbox = GetOrCreate(recipientId);
      mailbox.Closed = true;
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
  private sealed class Question(Guid recipientId, Guid senderId, Guid requestId, string callId, string prompt, bool wake) {
    public Guid QuestionId { get; } = Guid.NewGuid();
    public Guid RecipientId { get; } = recipientId;
    public Guid SenderId { get; } = senderId;
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

// One settled ask: the answer text, or the stated reason no answer arrived.
internal sealed record AgentQuestionResult(string? Answer, string? Error);

// A bounded observation of one unanswered ask: sender, pinned recipient and call identity.
internal sealed record QueuedQuestion(Guid QuestionId, Guid SenderId, Guid RecipientId, Guid RequestId,
    string CallId, string Prompt, bool Wake, bool Presented);

// The head captured for one recipient request. First marks the presentation written to history;
// Wakes lets that presentation create a request on its own instead of riding along with other work.
internal sealed record QuestionPresentation(Guid QuestionId, Guid SenderId, string Prompt, bool First, bool Wakes);
