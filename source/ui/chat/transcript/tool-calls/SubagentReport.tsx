import { useEffect, useState } from "preact/hooks";
import type { SessionClient } from "../../../sessions/SessionClient.ts";
import type { TranscriptToolFacts } from "../../../sessions/transcript/Messages.ts";
import { subagentReportPreview, subagentReportText } from "./SubagentReportText.ts";

const previewLimit = 240;
const completionRetryDelays = [250, 750, 1_500, 3_000];

interface ReportProps {
  client: SessionClient;
  sessionId: string;
  historyRevision: number;
  tool: TranscriptToolFacts;
  returned: boolean;
  visible: boolean;
  hasCompletion: boolean;
}

interface ReportText {
  text: string;
  truncated: boolean;
}

export function SubagentReport(props: ReportProps) {
  const { sessionId, historyRevision, tool, returned } = props;
  const key = JSON.stringify([sessionId, historyRevision, tool.requestId, tool.callId, returned]);
  return <Report key={key} {...props} />;
}

function Report({
  client,
  sessionId,
  historyRevision,
  tool,
  returned,
  visible,
  hasCompletion,
}: ReportProps) {
  const [preview, setPreview] = useState<ReportText | null>(null);
  const [detail, setDetail] = useState<ReportText | null>(null);
  const [expanded, setExpanded] = useState(false);
  const [error, setError] = useState(false);
  const [retry, setRetry] = useState(0);
  const ready = tool.isClosed || hasCompletion;
  const outcome = tool.outcome;
  const { requestId, callId } = tool;
  const full = expanded && preview?.truncated === true;
  const needsRead = full ? detail === null : preview === null;

  useEffect(() => {
    if (!visible || !ready || !needsRead) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let attempts = 0;
    setError(false);

    function read() {
      void client
        .readToolCallTranscript(
          sessionId,
          requestId,
          callId,
          { expectedHistoryRevision: historyRevision, returned, full },
          controller.signal,
        )
        .then((result) => {
          if (controller.signal.aborted) return;
          if (result?.output != null) {
            const report = {
              text: subagentReportText(result.output, returned, callId),
              truncated: result.truncated,
            };
            if (full) setDetail(report);
            else setPreview(report);
            return;
          }
          // The originating call can settle just before its background return is published.
          const delay = completionRetryDelays[attempts++];
          if (returned && outcome !== null && delay !== undefined) timer = setTimeout(read, delay);
          else if (hasCompletion || full) setError(true);
        })
        .catch(() => {
          if (!controller.signal.aborted) setError(true);
        });
    }
    read();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [
    client,
    sessionId,
    historyRevision,
    requestId,
    callId,
    returned,
    visible,
    ready,
    outcome,
    hasCompletion,
    full,
    needsRead,
    retry,
  ]);

  const excerpt = preview === null ? null : subagentReportPreview(preview.text, previewLimit);
  const needsDetail = preview !== null && (preview.truncated || excerpt !== preview.text);
  const outcomeText = tool.interrupted
    ? "Interrupted"
    : outcome === "failed"
      ? "Failed"
      : outcome === "cancelled"
        ? "Cancelled"
        : null;
  const shownDetail = expanded ? (detail ?? (preview?.truncated === false ? preview : null)) : null;
  const text = shownDetail?.text ?? excerpt;

  return (
    <div class="subagent-report">
      {text !== null && (
        <pre
          class="subagent-report-text"
          data-subagent-full={shownDetail !== null ? "" : undefined}
          tabIndex={shownDetail !== null ? 0 : undefined}
        >
          {outcomeText !== null && (text.length ? `${outcomeText}: ` : outcomeText)}
          {text}
          {shownDetail === null && needsDetail && "…"}
        </pre>
      )}
      {needsDetail && (
        <button
          type="button"
          class="subagent-report-action"
          aria-expanded={expanded}
          onClick={() => {
            if (expanded) setDetail(null);
            setError(false);
            setExpanded(!expanded);
          }}
        >
          {expanded ? "Show less" : "Show full report"}
        </button>
      )}
      {expanded && shownDetail === null && !error && (
        <span class="subagent-report-status" role="status">
          Loading full report…
        </span>
      )}
      {error && (
        <span class="subagent-report-status" role="status">
          Could not load report.{" "}
          <button
            type="button"
            class="subagent-report-action"
            onClick={() => setRetry((value) => value + 1)}
          >
            Retry
          </button>
        </span>
      )}
    </div>
  );
}
