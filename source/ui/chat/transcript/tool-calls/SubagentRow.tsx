import { useContext, useEffect, useMemo, useRef, useState } from "preact/hooks";
import { HoverPopover } from "../../../controls/HoverPopover.tsx";
import { Icon } from "../../../controls/Icon.tsx";
import { RequestError } from "../../../http/RequestError.ts";
import type { SessionClient } from "../../../sessions/SessionClient.ts";
import type { SubagentCall } from "../../../sessions/SubagentCall.ts";
import type { TranscriptToolFacts } from "../../../sessions/transcript/Messages.ts";
import { modelDisplayName } from "../../prepass/PrepassPresentation.ts";
import { SubagentNavigationContext } from "../../subagents/SubagentNavigation.ts";
import { SubagentCostControl } from "./SubagentCostControl.tsx";
import { shortSubagentModel, subagentElapsed } from "./SubagentMetrics.ts";
import type { SubagentRowPresentation } from "./SubagentPresentation.ts";
import { SubagentReport } from "./SubagentReport.tsx";
import { subscribeToToolRowClock } from "./ToolRowClock.ts";
import "./subagent-row.css";

export function SubagentRow({
  client,
  sessionId,
  historyRevision,
  tool,
  presentation,
  root,
  paused,
  modelNames,
}: {
  client: SessionClient;
  sessionId: string;
  historyRevision: number;
  tool: TranscriptToolFacts;
  presentation: Exclude<SubagentRowPresentation, { kind: "hidden" }>;
  root: HTMLElement | null;
  paused: boolean;
  modelNames?: ReadonlyMap<string, string>;
}) {
  const row = useRef<HTMLDivElement>(null);
  const [intersecting, setIntersecting] = useState(false);
  const navigate = useContext(SubagentNavigationContext);
  const visible = root !== null && intersecting && !paused;
  const child = useSubagentCall(client, sessionId, historyRevision, tool, visible);
  const orphan = presentation.kind === "completion";
  const title = child?.title.trim() || (orphan ? "Subagent result" : "Subagent call");
  const model = modelDisplayName(child?.modelId, modelNames ?? null);
  const [now, setNow] = useState(Date.now);
  const elapsed = subagentElapsed(child, tool, now);
  const showReport =
    (tool.isClosed || presentation.hasCompletion) &&
    (!child ||
      !navigate ||
      tool.interrupted ||
      (tool.outcome !== null && tool.outcome !== "completed"));

  useEffect(() => {
    if (!visible || !elapsed.running) return;
    setNow(Date.now());
    return subscribeToToolRowClock(setNow);
  }, [visible, elapsed.running]);

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
      <div class="subagent-line">
        {child && navigate ? (
          <button
            type="button"
            class="subagent-title"
            title={title}
            onClick={() => navigate(child)}
          >
            <span>{title}</span>
            <Icon name="chevron" />
          </button>
        ) : (
          <span class="subagent-title" title={title}>
            <span>{title}</span>
          </span>
        )}
        <span class="subagent-model" title={model ?? "Model unavailable"}>
          <span class="subagent-model-full">{model ?? "—"}</span>
          <span class="subagent-model-short">
            {model === null ? "—" : shortSubagentModel(model)}
          </span>
        </span>
        <span class="subagent-elapsed">
          {showReport ? (
            <HoverPopover
              label={`${elapsed.description}. Open subagent report`}
              surfaceLabel="Subagent report"
              class="subagent-metric-control"
              surfaceClass="subagent-report-popover"
              enabled={visible}
              content={
                <SubagentReport
                  client={client}
                  sessionId={sessionId}
                  historyRevision={historyRevision}
                  tool={tool}
                  returned={orphan || (tool.background && tool.started)}
                  visible={visible}
                  hasCompletion={presentation.hasCompletion}
                />
              }
            >
              <Icon name="return" />
              <span class="subagent-elapsed-value">{elapsed.text}</span>
            </HoverPopover>
          ) : (
            <span title={elapsed.description}>
              {elapsed.running && <span class="subagent-running" aria-hidden="true" />}
              <span class="subagent-elapsed-value">{elapsed.text}</span>
            </span>
          )}
        </span>
        <SubagentCostControl cost={child?.cost} visible={visible} />
      </div>
    </div>
  );
}

const childRetryDelays = [250, 750, 1_500, 3_000];

function useSubagentCall(
  client: SessionClient,
  sessionId: string,
  historyRevision: number,
  tool: TranscriptToolFacts,
  visible: boolean,
): SubagentCall | null {
  const { requestId, callId, started, isClosed, outcome } = tool;
  const lookup = useMemo(
    () => ({ client, sessionId, historyRevision, requestId, callId }),
    [client, sessionId, historyRevision, requestId, callId],
  );
  const [resolved, setResolved] = useState<{ lookup: typeof lookup; child: SubagentCall } | null>(
    null,
  );
  const child = resolved?.lookup === lookup ? resolved.child : null;

  useEffect(() => {
    if (!visible || (!started && !isClosed)) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let attempt = 0;
    let lastLink: SubagentCall | null = null;
    function retrySoon() {
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
            if (!isClosed || attempt < childRetryDelays.length) retrySoon();
            return;
          }
          if (link.parentId !== lookup.sessionId) return;
          lastLink = link;
          setResolved({ lookup, child: link });
          if (link.titlePending) retrySoon();
          else if (link.live) timer = setTimeout(read, 2_000);
        })
        .catch((failure: unknown) => {
          if (
            !controller.signal.aborted &&
            (!isClosed || lastLink?.live || attempt < childRetryDelays.length) &&
            (!(failure instanceof RequestError) || failure.status >= 500)
          )
            retrySoon();
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
