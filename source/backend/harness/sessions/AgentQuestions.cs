namespace Urd.Core.Sessions;

// The in-memory question broker owned by SessionManager. Each recipient has one FIFO mailbox
// whose head is the only current question. Records settle exactly once — answered, cancelled
// by the waiting ask, or failed when the mailbox closes — and completions are signalled
// outside the lock. Delivery, wakes and answer routing are wired around this state.
internal sealed class AgentQuestions {
  // Every failed record carries the same blame: the recipient ended without answering.
  internal const string RecipientFinished = "The recipient agent finished without answering the question.";

  private readonly object stateLock = new();
  private readonly Dictionary<Guid, Mailbox> mailboxes = [];

  // Enqueues one question in FIFO order and returns its settling task. The ask's cancellation
  // removes its own record whenever it has not already settled; a closed mailbox returns the
  // task already failed, so the ask never waits on a recipient that cannot answer.
  internal Task<AgentQuestionResult> Enqueue(Guid recipientId, Guid senderId, Guid requestId, string callId,
      string prompt, bool wake, CancellationToken cancellation) {
    ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
    var record = new Question(recipientId, senderId, requestId, callId, prompt, wake);
    Mailbox mailbox;
    lock (stateLock) {
      if (!mailboxes.TryGetValue(recipientId, out mailbox!)) {
        mailbox = new Mailbox();
        mailboxes[recipientId] = mailbox;
      }

      if (mailbox.Closed) {
        record.Completion.TrySetResult(new(null, RecipientFinished));
        return record.Completion.Task;
      }

      if (cancellation.IsCancellationRequested) {
        record.Completion.TrySetCanceled(cancellation);
        return record.Completion.Task;
      }

      mailbox.Questions.Add(record);
    }

    // A token firing between the queued state and this registration re-enters Cancel below.
    record.Registration = cancellation.Register(() => Cancel(record.RecipientId, record.QuestionId));
    return record.Completion.Task;
  }

  // Cancels one waiting record exactly once; absent or already settled records stay untouched.
  internal void Cancel(Guid recipientId, Guid questionId) {
    Question? question = Remove(recipientId, questionId);
    if (question is null)
      return;

    question.Registration.Dispose();
    question.Completion.TrySetCanceled(CancellationToken.None);
  }

  // Answers exactly one record by question identity. False when it is absent or already settled.
  internal bool Answer(Guid recipientId, Guid questionId, string answer) {
    ArgumentException.ThrowIfNullOrWhiteSpace(answer);
    Question? question = Remove(recipientId, questionId);
    if (question is null)
      return false;

    question.Registration.Dispose();
    question.Completion.TrySetResult(new(answer, null));
    return true;
  }

  // Answers one recipient's current head question; false when nothing is queued.
  internal bool AnswerCurrent(Guid recipientId, string answer) {
    ArgumentException.ThrowIfNullOrWhiteSpace(answer);
    Question? head;
    lock (stateLock) {
      if (!mailboxes.TryGetValue(recipientId, out Mailbox? mailbox) || mailbox.Questions.Count == 0)
        return false;

      head = Detach(mailbox, mailbox.Questions[0]);
    }

    head.Registration.Dispose();
    head.Completion.TrySetResult(new(answer, null));
    return true;
  }

  // Closes one recipient's mailbox: further asks fail immediately and every remaining record
  // fails as unanswered. Completions are signalled after the lock releases.
  internal void CloseRecipient(Guid recipientId) {
    Question[] unsettled;
    lock (stateLock) {
      Mailbox mailbox = GetOrCreate(recipientId);
      mailbox.Closed = true;
      unsettled = [.. mailbox.Questions];
      foreach (Question question in unsettled)
        question.Settled = true;
      mailbox.Questions.Clear();
    }

    foreach (Question question in unsettled) {
      question.Registration.Dispose();
      question.Completion.TrySetResult(new(null, RecipientFinished));
    }
  }

  // Closes every mailbox when the whole manager stops; no ask survives a restart.
  internal void CloseAll() {
    Question[] unsettled;
    lock (stateLock) {
      unsettled = [.. mailboxes.Values.SelectMany(mailbox => mailbox.Questions)];
      foreach (Mailbox mailbox in mailboxes.Values) {
        mailbox.Closed = true;
        foreach (Question question in mailbox.Questions)
          question.Settled = true;
        mailbox.Questions.Clear();
      }
    }

    foreach (Question question in unsettled) {
      question.Registration.Dispose();
      question.Completion.TrySetResult(new(null, RecipientFinished));
    }
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
    public CancellationTokenRegistration Registration { get; set; }
    public TaskCompletionSource<AgentQuestionResult> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public QueuedQuestion Observe() => new(QuestionId, SenderId, RecipientId, RequestId, CallId, Prompt, Wake);
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
    string CallId, string Prompt, bool Wake);
