import { useEffect, useRef, useState } from "preact/hooks";
import { RequestError } from "../../http/RequestError.ts";
import type { SessionClient } from "../../sessions/SessionClient.ts";
import type { UserQuestion } from "../../sessions/inputs/UserQuestion.ts";
import "./user-questions.css";

// Keep drafts across chat and subagent navigation without persisting answers to browser storage.
const drafts = new Map<string, string>();
const maxDrafts = 32;

function updateDraft(key: string, draft: string) {
  drafts.delete(key);
  if (draft) drafts.set(key, draft);
  if (drafts.size > maxDrafts) drafts.delete(drafts.keys().next().value!);
}

function discardSettledDrafts(sessionId: string, questions: readonly UserQuestion[]) {
  const prefix = `${sessionId}:`;
  for (const key of drafts.keys())
    if (key.startsWith(prefix) && !questions.some(({ id }) => id === key.slice(prefix.length)))
      drafts.delete(key);
}

interface UserQuestionsProps {
  client: SessionClient;
  sessionId: string;
  connected: boolean;
  notice: string | null;
}

export function UserQuestions({ client, sessionId, connected, notice }: UserQuestionsProps) {
  const [questions, setQuestions] = useState<UserQuestion[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [, setDraftVersion] = useState(0);
  const [sending, setSending] = useState(false);
  const [readFailed, setReadFailed] = useState(false);
  const [answerError, setAnswerError] = useState<string | null>(null);
  const refresh = useRef<() => void>(() => {});
  const sendingGate = useRef(false);
  const revision = useRef(0);

  useEffect(() => {
    let disposed = false;
    let reading = false;
    let dirty = false;
    const controller = new AbortController();
    async function read() {
      if (reading) {
        dirty = true;
        return;
      }
      reading = true;
      do {
        dirty = false;
        const started = revision.current;
        try {
          const result = await client.getUserQuestions(sessionId, controller.signal);
          if (!disposed && started === revision.current) {
            discardSettledDrafts(sessionId, result);
            setQuestions(result);
            setReadFailed(false);
          }
        } catch {
          if (!disposed) setReadFailed(true);
        }
      } while (dirty && !disposed);
      reading = false;
    }
    refresh.current = () => {
      void read();
    };
    return () => {
      disposed = true;
      controller.abort();
      refresh.current = () => {};
    };
  }, [client, sessionId]);

  useEffect(() => {
    if (connected) refresh.current();
  }, [connected, notice]);

  const question = questions.find((item) => item.id === selected) ?? questions[0];
  const draftKey = question ? `${sessionId}:${question.id}` : "";
  const draft = drafts.get(draftKey) ?? "";

  async function answer(value: string) {
    if (!question || !connected || sendingGate.current || !value.trim() || value.length > 8000)
      return;
    sendingGate.current = true;
    setSending(true);
    setAnswerError(null);
    try {
      const receipt = await client.answerUserQuestion(sessionId, question.id, value);
      if (receipt?.answer !== value) throw new Error("Invalid answer receipt.");
      revision.current++;
      drafts.delete(draftKey);
      setQuestions((current) => current.filter((item) => item.id !== question.id));
      setSelected(null);
      refresh.current();
    } catch (failure) {
      if (failure instanceof RequestError && failure.status === 409) {
        setAnswerError("This question is no longer pending.");
      } else {
        setAnswerError("Answer not confirmed.");
      }
      refresh.current();
    } finally {
      sendingGate.current = false;
      setSending(false);
    }
  }

  if (!question && !readFailed && !answerError) return null;
  return (
    <section class="user-questions" aria-label="Pending questions">
      {question && (
        <>
          <div class="user-question-heading">
            <span>{question.sourceTitle ?? "Assistant"}</span>
            {questions.length > 1 && (
              <span>
                <button
                  type="button"
                  aria-label="Previous question"
                  onClick={() =>
                    setSelected(
                      questions[
                        (questions.indexOf(question) + questions.length - 1) % questions.length
                      ]!.id,
                    )
                  }
                >
                  ‹
                </button>
                {questions.indexOf(question) + 1} / {questions.length}
                <button
                  type="button"
                  aria-label="Next question"
                  onClick={() =>
                    setSelected(questions[(questions.indexOf(question) + 1) % questions.length]!.id)
                  }
                >
                  ›
                </button>
              </span>
            )}
          </div>
          <div class="user-question-text">{question.question}</div>
          {question.context && <div class="user-question-context">{question.context}</div>}
          {question.answers.length > 0 && (
            <div class="user-question-options">
              {question.answers.map((option, index) => (
                <button
                  key={index}
                  type="button"
                  disabled={!connected || sending}
                  onClick={() => void answer(option)}
                >
                  {option}
                </button>
              ))}
            </div>
          )}
          <form
            class="user-question-form"
            onSubmit={(event) => {
              event.preventDefault();
              void answer(draft);
            }}
          >
            <textarea
              aria-label="Custom answer"
              placeholder="Custom answer"
              rows={1}
              maxLength={8000}
              value={draft}
              onInput={(event) => {
                const value = event.currentTarget.value;
                updateDraft(draftKey, value);
                setDraftVersion((version) => version + 1);
              }}
            />
            <button type="submit" disabled={!connected || sending || !draft.trim()}>
              Submit answer
            </button>
          </form>
          {sending && <div role="status">Submitting answer…</div>}
        </>
      )}
      {readFailed && (
        <div class="user-question-feedback" role="alert">
          Couldn’t load questions.{" "}
          <button type="button" onClick={() => refresh.current()}>
            Retry
          </button>
        </div>
      )}
      {answerError && (
        <div class="user-question-feedback" role="alert">
          {answerError}{" "}
          <button type="button" onClick={() => setAnswerError(null)}>
            Dismiss
          </button>
        </div>
      )}
    </section>
  );
}
