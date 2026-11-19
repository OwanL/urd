namespace Urd.Core.Execution;

// Distinguishes executions started by user input, compaction or continuation.
internal enum ExecutionRunKind {
  UserInput = 0,
  Compaction = 1,
  Continuation = 2
}
