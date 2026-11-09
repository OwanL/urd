import { useEffect, useRef, useState } from "preact/hooks";
import type { NotSentInput, NotSentPage } from "../sessions/Messages.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionSnapshot } from "../sessions/SessionSnapshot.ts";
import type {
  UserMessageReceipt,
  UserMessageSubmission,
} from "../sessions/UserMessageSubmission.ts";
import type { WorkspaceCommands } from "./WorkspaceCommands.ts";

interface NotSentInputsProps {
  client: SessionClient;
  sessionId: string;
  snapshot: SessionSnapshot | null;
  commands: WorkspaceCommands;
  canResend: boolean;
}

interface RetrievedInput {
  submissionId: string;
  submission: UserMessageSubmission;
}

export function NotSentInputs({
  client,
  sessionId,
  snapshot,
  commands,
  canResend,
}: NotSentInputsProps) {
  const [open, setOpen] = useState(false);
  const [page, setPage] = useState<NotSentPage | null>(null);
  const [pageCursor, setPageCursor] = useState<string | null>(null);
  const [previousCursors, setPreviousCursors] = useState<(string | null)[]>([]);
  const [pageNumber, setPageNumber] = useState(1);
  const [loadingPage, setLoadingPage] = useState(false);
  const [pageError, setPageError] = useState<string | null>(null);
  const [retrieved, setRetrieved] = useState<RetrievedInput | null>(null);
  const [retrieving, setRetrieving] = useState<string | null>(null);
  const [sourceError, setSourceError] = useState<{ id: string; message: string } | null>(null);
  const [resending, setResending] = useState<string | null>(null);
  const [resendResult, setResendResult] = useState<{
    id: string;
    submissionId: string | null;
    message: string;
  } | null>(null);
  const listController = useRef<AbortController | null>(null);
  const receiptController = useRef<AbortController | null>(null);

  useEffect(
    () => () => {
      listController.current?.abort();
      receiptController.current?.abort();
    },
    [],
  );

  async function readPage(cursor: string | null) {
    listController.current?.abort();
    const controller = new AbortController();
    listController.current = controller;
    setLoadingPage(true);
    setPage(null);
    setPageCursor(cursor);
    setPageError(null);
    setRetrieved(null);
    setSourceError(null);
    setResendResult(null);
    try {
      const result = await client.getNotSent(sessionId, cursor ?? undefined, controller.signal);
      if (controller.signal.aborted) return;
      setPage(result);
    } catch (cause) {
      if (!controller.signal.aborted)
        setPageError(cause instanceof Error ? cause.message : "Not-sent inputs could not be read.");
    } finally {
      if (listController.current === controller) {
        listController.current = null;
        setLoadingPage(false);
      }
    }
  }

  function show() {
    setOpen(true);
    setPage(null);
    setPreviousCursors([]);
    setPageNumber(1);
    void readPage(null);
  }

  function hide() {
    listController.current?.abort();
    receiptController.current?.abort();
    listController.current = null;
    receiptController.current = null;
    setOpen(false);
    setPage(null);
    setPageCursor(null);
    setPreviousCursors([]);
    setPageNumber(1);
    setLoadingPage(false);
    setPageError(null);
    setRetrieved(null);
    setRetrieving(null);
    setSourceError(null);
    setResending(null);
    setResendResult(null);
  }

  function nextPage() {
    const next = page?.nextCursor;
    if (next === null || next === undefined || loadingPage) return;
    setPreviousCursors((cursors) => [...cursors, pageCursor]);
    setPageNumber((number) => number + 1);
    void readPage(next);
  }

  function previousPage() {
    if (!previousCursors.length || loadingPage) return;
    const previous = previousCursors.at(-1) ?? null;
    setPreviousCursors((cursors) => cursors.slice(0, -1));
    setPageNumber((number) => Math.max(1, number - 1));
    void readPage(previous);
  }

  async function retrieve(input: NotSentInput) {
    receiptController.current?.abort();
    const controller = new AbortController();
    receiptController.current = controller;
    setRetrieving(input.submissionId);
    setRetrieved(null);
    setSourceError(null);
    setResendResult(null);
    try {
      const receipt = await client.getMessageReceipt(
        sessionId,
        input.submissionId,
        controller.signal,
      );
      if (controller.signal.aborted) return;
      if (!isMatchingReceipt(receipt, sessionId, input))
        throw new Error("The original input receipt is unavailable.");
      setRetrieved({ submissionId: input.submissionId, submission: receipt.submission });
    } catch (cause) {
      if (!controller.signal.aborted)
        setSourceError({
          id: input.submissionId,
          message: cause instanceof Error ? cause.message : "The original input could not be read.",
        });
    } finally {
      if (receiptController.current === controller) {
        receiptController.current = null;
        setRetrieving(null);
      }
    }
  }

  async function resend() {
    const source = retrieved;
    if (!source || !snapshot || !canResend || resending !== null) return;
    setResending(source.submissionId);
    setResendResult(null);
    try {
      const result = await commands.resendNotSent(sessionId, snapshot, source.submission);
      setResendResult({
        id: source.submissionId,
        submissionId: result?.submissionId ?? null,
        message: resendMessage(result?.receipt ?? null, sessionId, result?.submissionId ?? null),
      });
    } catch (cause) {
      setResendResult({
        id: source.submissionId,
        submissionId: null,
        message: `Resend uncertain: ${cause instanceof Error ? cause.message : String(cause)}`,
      });
    } finally {
      setResending(null);
    }
  }

  return (
    <div>
      <button
        aria-expanded={open}
        aria-controls={`not-sent-${sessionId}`}
        onClick={() => (open ? hide() : show())}
      >
        {open ? "Hide not sent" : "Not sent"}
      </button>
      {open && (
        <section id={`not-sent-${sessionId}`} aria-label="Not-sent inputs">
          {loadingPage && <span>Loading… </span>}
          {pageError && (
            <span class="inline-error">
              {pageError} <button onClick={() => void readPage(pageCursor)}>Retry</button>
            </span>
          )}
          {page && !loadingPage && page.inputs.length === 0 && <p>No inputs.</p>}
          {page && page.inputs.length > 0 && (
            <>
              <p>Page {pageNumber}</p>
              <ol>
                {page.inputs.map((input) => {
                  const source = retrieved?.submissionId === input.submissionId ? retrieved : null;
                  const resendState = resendResult?.id === input.submissionId ? resendResult : null;
                  const resent = resendState?.message;
                  const accepted = resent === "Resend accepted.";
                  return (
                    <li key={input.submissionId}>
                      <span>{input.preview}</span>{" "}
                      <button
                        disabled={loadingPage || retrieving !== null}
                        onClick={() => void retrieve(input)}
                      >
                        {retrieving === input.submissionId ? "Retrieving…" : "Retrieve"}
                      </button>
                      {source && (
                        <>
                          <pre>{source.submission.text}</pre>
                          <button
                            disabled={!canResend || resending !== null || accepted}
                            onClick={() => void resend()}
                          >
                            {resending === input.submissionId
                              ? "Resending…"
                              : accepted
                                ? "Accepted"
                                : "Resend"}
                          </button>
                          {resent && <span> {resent}</span>}
                        </>
                      )}
                      {sourceError?.id === input.submissionId && retrieving === null && !source && (
                        <span class="inline-error"> {sourceError.message}</span>
                      )}
                    </li>
                  );
                })}
              </ol>
              <button disabled={previousCursors.length === 0 || loadingPage} onClick={previousPage}>
                Previous page
              </button>{" "}
              {page.nextCursor !== null && (
                <button disabled={loadingPage} onClick={nextPage}>
                  Next page
                </button>
              )}
            </>
          )}
        </section>
      )}
    </div>
  );
}

function isMatchingReceipt(
  receipt: UserMessageReceipt | null,
  sessionId: string,
  input: NotSentInput,
): receipt is UserMessageReceipt {
  return (
    receipt !== null &&
    receipt.sessionId === sessionId &&
    receipt.acceptance === "accepted" &&
    receipt.executionId === input.executionId &&
    receipt.submission.submissionId === input.submissionId
  );
}

function resendMessage(
  receipt: UserMessageReceipt | null,
  sessionId: string,
  submissionId: string | null,
): string {
  if (
    receipt === null ||
    submissionId === null ||
    receipt.sessionId !== sessionId ||
    receipt.submission.submissionId !== submissionId
  )
    return "Resend uncertain.";
  if (receipt.acceptance === "accepted") return "Resend accepted.";
  return `Not resent: ${receipt.refusalReason}.`;
}
