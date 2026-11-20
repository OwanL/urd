import type { RefObject } from "preact";
import { useEffect, type Dispatch, type StateUpdater } from "preact/hooks";
import type { SessionClient } from "../../../sessions/SessionClient.ts";
import type { ActiveTarget, Preview, PreviewIdentity } from "./ToolRowInteractionTypes.ts";

export function useToolPreview(
  active: ActiveTarget | null,
  activeRef: RefObject<ActiveTarget | null>,
  client: SessionClient,
  sessionId: string,
  previewVersion: number,
  setPreview: Dispatch<StateUpdater<Preview | null>>,
) {
  useEffect(() => {
    const target = activeRef.current;
    if (!target || target.kind !== "inspection") return;
    const controller = new AbortController();
    const identity: PreviewIdentity = {
      id: target.id,
      requestId: target.requestId,
      callId: target.callId,
      historyRevision: target.historyRevision,
      payloadFinished: target.payloadFinished,
    };
    setPreview((current) =>
      current?.id === target.id && current.state === "ready"
        ? current
        : { ...identity, state: "loading" },
    );
    void client
      .readToolCallTranscript(
        sessionId,
        target.requestId,
        target.callId,
        {
          expectedHistoryRevision: target.historyRevision,
          returned: target.returned,
          full: false,
        },
        controller.signal,
      )
      .then((transcript) => {
        if (controller.signal.aborted || activeRef.current?.id !== target.id) return;
        setPreview(
          transcript
            ? { ...identity, state: "ready", transcript }
            : { ...identity, state: "missing" },
        );
      })
      .catch(() => {
        if (!controller.signal.aborted && activeRef.current?.id === target.id)
          setPreview({ ...identity, state: "error" });
      });
    return () => controller.abort();
    // The ref tracks the latest target for asynchronous completion, not effect scheduling.
    // oxlint-disable-next-line react-hooks/exhaustive-deps
  }, [active?.id, activeRef, client, previewVersion, sessionId, setPreview]);
}
