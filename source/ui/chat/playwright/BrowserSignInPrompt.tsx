import { useEffect, useRef, useState } from "preact/hooks";
import type { SessionClient } from "../../sessions/SessionClient.ts";
import type { BrowserSignIn, BrowserSignInAction } from "./BrowserSignIn.ts";
import "./browser-sign-in.css";

interface BrowserSignInPromptProps {
  client: SessionClient;
  sessionId: string;
  connected: boolean;
  notice: string | null;
}

export function BrowserSignInPrompt({ client, sessionId, connected, notice }: BrowserSignInPromptProps) {
  const [handoff, setHandoff] = useState<BrowserSignIn | null>(null);
  const [sending, setSending] = useState(false);
  const [readFailed, setReadFailed] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);
  const refresh = useRef<() => void>(() => {});
  const revision = useRef(0);
  const gate = useRef(false);
  const generation = useRef(0);

  useEffect(() => {
    let disposed = false;
    let reading = false;
    let dirty = false;
    const controller = new AbortController();
    generation.current++;
    gate.current = false;
    setSending(false);
    setHandoff(null);
    setReadFailed(false);
    setActionError(null);
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
          const result = await client.getBrowserSignIn(sessionId, controller.signal);
          if (!disposed && started === revision.current && !gate.current) {
            setHandoff(result.handoff);
            setReadFailed(false);
          }
        } catch {
          if (!disposed && started === revision.current && !gate.current) setReadFailed(true);
        }
      } while (dirty && !disposed);
      reading = false;
    }
    refresh.current = () => void read();
    return () => {
      disposed = true;
      generation.current++;
      controller.abort();
      refresh.current = () => {};
    };
  }, [client, sessionId]);

  useEffect(() => {
    if (connected) refresh.current();
  }, [connected, notice]);

  async function act(action: BrowserSignInAction) {
    if (!handoff || !connected || gate.current) return;
    const started = generation.current;
    gate.current = true;
    revision.current++;
    setSending(true);
    setActionError(null);
    try {
      await client.browserSignInAction(sessionId, handoff.id, action);
      if (started === generation.current)
        setHandoff(action === "open" ? { ...handoff, stage: "opening" } : null);
    } catch {
      if (started === generation.current) setActionError("Action not confirmed. Check the current sign-in state.");
    } finally {
      if (started === generation.current) {
        revision.current++;
        gate.current = false;
        setSending(false);
        refresh.current();
      }
    }
  }

  if (!handoff && !readFailed && !actionError) return null;
  return (
    <section class="browser-sign-in" aria-label="Browser sign-in" aria-busy={sending}>
      {handoff && (
        <>
          <div class="browser-sign-in-details">
            <strong>Sign in to {handoff.origin}</strong>
            <span>Sign in in the browser window. Other local chats can reuse this sign-in.</span>
            {handoff.stage === "opening" && <span role="status">Opening browser…</span>}
          </div>
          <div class="browser-sign-in-actions">
            {handoff.stage === "requested" && (
              <button type="button" disabled={!connected || sending} onClick={() => void act("open")}>
                Sign in
              </button>
            )}
            {handoff.stage === "visible" && (
              <button type="button" disabled={!connected || sending} onClick={() => void act("continue")}>
                Continue
              </button>
            )}
            <button type="button" disabled={!connected || sending} onClick={() => void act("cancel")}>
              Cancel
            </button>
          </div>
        </>
      )}
      {readFailed && (
        <div class="browser-sign-in-error" role="alert">
          Couldn’t load sign-in. <button type="button" disabled={!connected || sending} onClick={() => refresh.current()}>Retry</button>
        </div>
      )}
      {actionError && (
        <div class="browser-sign-in-error" role="alert">
          {actionError} <button type="button" onClick={() => setActionError(null)}>Dismiss</button>
        </div>
      )}
    </section>
  );
}
