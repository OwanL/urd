import { useContext, useEffect, useMemo, useRef, useState } from "preact/hooks";
import { Icon } from "../../../controls/Icon.tsx";
import { RequestError } from "../../../http/RequestError.ts";
import type { SessionClient } from "../../../sessions/SessionClient.ts";
import type { TranscriptToolFacts } from "../../../sessions/transcript/Messages.ts";
import {
  SubagentNavigationContext,
  type SubagentLink,
} from "../../subagents/SubagentNavigation.ts";
import type { SubagentRowPresentation } from "./SubagentPresentation.ts";
import { SubagentReport } from "./SubagentReport.tsx";
import "./subagent-row.css";

export function SubagentRow({
  client,
  sessionId,
  historyRevision,
  tool,
  presentation,
  root,
  paused,
}: {
  client: SessionClient;
  sessionId: string;
  historyRevision: number;
  tool: TranscriptToolFacts;
  presentation: Exclude<SubagentRowPresentation, { kind: "hidden" }>;
  root: HTMLElement | null;
  paused: boolean;
}) {
  const row = useRef<HTMLDivElement>(null);
  const [intersecting, setIntersecting] = useState(false);
  const navigate = useContext(SubagentNavigationContext);
  const visible = root !== null && intersecting && !paused;
  const child = useSubagentLink(client, sessionId, historyRevision, tool, visible);
  const orphan = presentation.kind === "completion";
  const title = child?.title.trim() || (orphan ? "Subagent result" : "Subagent call");

  useEffect(() => {
    const node = row.current;
    if (!node || !root) return;
    let observing = true;
    const observer = new IntersectionObserver(
      ([intersection]) => {
        if (observing) setIntersecting(intersection?.isIntersecting ?? false);
      },
      { root },
    );
    observer.observe(node);
    return () => {
      observing = false;
      observer.disconnect();
    };
  }, [root]);

  return (
    <div ref={row} class="subagent-row" data-subagent-row="" data-tool-call-id={tool.callId}>
      {presentation.heading !== null && <h3 class="subagent-heading">{presentation.heading}</h3>}
      {child && navigate ? (
        <button type="button" class="subagent-title" title={title} onClick={() => navigate(child)}>
          <span>{title}</span>
          <Icon name="chevron" />
        </button>
      ) : (
        <div class="subagent-title" title={title}>
          <span>{title}</span>
        </div>
      )}
      <SubagentReport
        client={client}
        sessionId={sessionId}
        historyRevision={historyRevision}
        tool={tool}
        returned={orphan || (tool.background && tool.started)}
        visible={visible}
        hasCompletion={presentation.hasCompletion}
      />
    </div>
  );
}

const childRetryDelays = [250, 750, 1_500, 3_000];

function useSubagentLink(
  client: SessionClient,
  sessionId: string,
  historyRevision: number,
  tool: TranscriptToolFacts,
  visible: boolean,
): SubagentLink | null {
  const { requestId, callId, started, isClosed, outcome } = tool;
  const lookup = useMemo(
    () => ({ client, sessionId, historyRevision, requestId, callId }),
    [client, sessionId, historyRevision, requestId, callId],
  );
  const [resolved, setResolved] = useState<{ lookup: typeof lookup; child: SubagentLink } | null>(
    null,
  );
  const child = resolved?.lookup === lookup ? resolved.child : null;

  useEffect(() => {
    if (!visible) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let attempt = 0;
    let titlePending = false;
    function retry() {
      if (controller.signal.aborted) return;
      const delay = childRetryDelays[Math.min(attempt++, childRetryDelays.length - 1)];
      timer = setTimeout(read, delay);
    }
    function read() {
      void lookup.client
        .getSubagentForCall(lookup.sessionId, lookup.requestId, lookup.callId, controller.signal)
        .then((link) => {
          if (controller.signal.aborted) return;
          if (link === null) {
            // Closed legacy calls may never have a unique link, so bound their retries.
            if (!isClosed || attempt < childRetryDelays.length) retry();
            return;
          }
          if (link.parentId !== lookup.sessionId) return;
          setResolved({ lookup, child: link });
          titlePending = link.titlePending;
          if (titlePending) retry();
        })
        .catch((failure: unknown) => {
          if (titlePending && (!(failure instanceof RequestError) || failure.status >= 500))
            retry();
        });
    }
    read();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [lookup, visible, started, isClosed, outcome]);
  return child;
}
