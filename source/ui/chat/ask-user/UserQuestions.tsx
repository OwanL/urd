import { useEffect, useRef, useState } from "preact/hooks";
import { IconButton } from "../../controls/IconButton.tsx";
import { RequestError } from "../../http/RequestError.ts";
import type { SessionClient } from "../../sessions/SessionClient.ts";
import type { UserQuestion } from "../../sessions/inputs/UserQuestion.ts";
import { QuestionPrompt } from "./QuestionPrompt.tsx";
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
  const panel = useRef<HTMLElement>(null);
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

  const selectedIndex = Math.max(
    0,
    questions.findIndex((item) => item.id === selected),
  );
  const question = questions[selectedIndex];
  const draftKey = question ? `${sessionId}:${question.id}` : "";
  const draft = drafts.get(draftKey) ?? "";
  const move = (offset: number) =>
    setSelected(questions[(selectedIndex + offset + questions.length) % questions.length]!.id);

  async function answer(value: string) {
    if (!question || !connected || sendingGate.current || !value.trim() || value.length > 8000)
      return;
    const focusedInPanel = panel.current?.contains(document.activeElement);
    sendingGate.current = true;
    setSending(true);
    setAnswerError(null);
    try {
      const receipt = await client.answerUserQuestion(sessionId, question.id, value);
      if (receipt?.answer !== value) throw new Error("Invalid answer receipt.");
      revision.current++;
      drafts.delete(draftKey);
      if (questions.length > 1 && focusedInPanel) {
        const active = document.activeElement;
        if (active === document.body || panel.current?.contains(active))
          panel.current?.focus({ preventScroll: true });
      }
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
    <section ref={panel} class="user-questions" aria-label="Pending questions" tabIndex={-1}>
      {question && (
        <div class="user-question-current" aria-busy={sending}>
          <QuestionPrompt
            key={question.id}
            question={question}
            draft={draft}
            disabled={!connected || sending}
            onDraft={(text) => {
              updateDraft(draftKey, text);
              setDraftVersion((version) => version + 1);
            }}
            onAnswer={(text) => void answer(text)}
          />
          {questions.length > 1 && (
            <div class="user-question-pages">
              <IconButton
                class="user-question-previous"
                icon="chevron"
                label="Previous question"
                onClick={() => move(-1)}
              />
              <span>
                {selectedIndex + 1} / {questions.length}
              </span>
              <IconButton
                class="user-question-next"
                icon="chevron"
                label="Next question"
                onClick={() => move(1)}
              />
            </div>
          )}
        </div>
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
