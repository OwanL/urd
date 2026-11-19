import type {
  ComponentChildren,
  TargetedFocusEvent,
  TargetedKeyboardEvent,
  TargetedPointerEvent,
} from "preact";
import { useEffect, useId, useRef, useState } from "preact/hooks";
import type { SessionClient } from "../../sessions/SessionClient.ts";
import type { PrepassSummary, ResponseHeaderInfo } from "../../sessions/transcript/Prepass.ts";
import type { TranscriptEntryKind } from "../../sessions/transcript/Messages.ts";
import { Tooltip } from "../../controls/Tooltip.tsx";
import { Icon } from "../../controls/Icon.tsx";
import { PrepassDetailsView } from "./PrepassDetails.tsx";
import {
  effortLabel,
  formatClockTime,
  modelDisplayName,
  orderedPrepass,
  prepassRowModel,
  prepassOutcome,
  prepassUsedSessionModel,
} from "./PrepassValues.ts";
import type { PrepassRowModel } from "./PrepassValues.ts";
import "./response-status.css";

// Shared facts every preparation detail read needs within one conversation.
export interface ResponseStatusContext {
  readonly client: SessionClient;
  readonly sessionId: string;
  readonly modelNames: ReadonlyMap<string, string>;
  readonly visible: boolean;
  // Opens Settings on the model-buckets tab; absent when the surface cannot open Settings.
  readonly onChooseModels?: (() => void) | undefined;
}

function scrollTooltipDetails(event: TargetedKeyboardEvent<HTMLSpanElement>) {
  if (
    event.altKey ||
    event.ctrlKey ||
    event.metaKey ||
    event.shiftKey ||
    event.target !== event.currentTarget
  )
    return;
  if (
    event.key !== "ArrowDown" &&
    event.key !== "ArrowUp" &&
    event.key !== "PageDown" &&
    event.key !== "PageUp" &&
    event.key !== "Home" &&
    event.key !== "End"
  )
    return;

  const details = event.currentTarget
    .getAttribute("aria-describedby")
    ?.split(/\s+/)
    .map((id) => document.getElementById(id))
    .find((surface) => surface?.querySelector(".prepass-details"))
    ?.querySelector<HTMLElement>(".prepass-details");
  if (!details) return;

  const line = 40;
  const page = Math.max(line, details.clientHeight - line);
  let scrollTop = details.scrollTop;
  switch (event.key) {
    case "ArrowDown":
      scrollTop += line;
      break;
    case "ArrowUp":
      scrollTop -= line;
      break;
    case "PageDown":
      scrollTop += page;
      break;
    case "PageUp":
      scrollTop -= page;
      break;
    case "Home":
      scrollTop = 0;
      break;
    case "End":
      scrollTop = details.scrollHeight;
      break;
  }
  event.preventDefault();
  details.scrollTop = scrollTop;
}

export function ResponseHeader({
  response,
  modelNames,
}: {
  response: ResponseHeaderInfo;
  modelNames: ReadonlyMap<string, string>;
}) {
  const model = modelDisplayName(response.modelId, modelNames);
  const effort = effortLabel(response.reasoningEffort);
  const time = response.startedAt ? formatClockTime(response.startedAt) : null;
  if (!model && !effort && !time) return null;
  return (
    <p class="response-header">
      {model ? <span class="response-header-model">{model}</span> : null}
      {effort ? <span class="response-header-effort">{effort}</span> : null}
      {time ? <time class="response-header-time">{time}</time> : null}
    </p>
  );
}

export function PrepassRows({
  preparation,
  context,
  historyRevision,
}: {
  preparation: readonly PrepassSummary[];
  context: ResponseStatusContext;
  historyRevision: number;
}) {
  const rows = orderedPrepass(preparation);
  const outcome = prepassOutcome(preparation);
  const listId = useId();
  const groupRef = useRef<HTMLDivElement>(null);
  const [expanded, setExpanded] = useState(false);
  const [pointerHold, setPointerHold] = useState(false);
  const [focusHold, setFocusHold] = useState(false);

  // A fresh run always settles back to the collapsed default; manual state never survives it.
  useEffect(() => {
    if (outcome === null) setExpanded(false);
  }, [outcome]);

  // Hidden groups cannot be inspected; orphaned holds would pin rows open invisibly.
  useEffect(() => {
    if (!context.visible) {
      setPointerHold(false);
      setFocusHold(false);
    }
  }, [context.visible]);

  if (rows.length === 0) return null;
  const open = outcome === null || expanded || pointerHold || focusHold;
  // Do not shift inspected rows (and their open tooltip) by inserting the summary above them.
  const toggleVisible = outcome !== null && (expanded || (!pointerHold && !focusHold));
  const usedSessionModel = prepassUsedSessionModel(preparation);
  const leftGroup = (
    event: TargetedPointerEvent<HTMLDivElement> | TargetedFocusEvent<HTMLDivElement>,
  ) => {
    const next = event.relatedTarget as Node | null;
    return !next || !groupRef.current?.contains(next);
  };
  return (
    <div
      ref={groupRef}
      class="response-status-rows"
      aria-label="Session preparation"
      onPointerEnter={() => {
        // Inspection of an open group survives a new run; collapsed hover never reopens it.
        if (open) setPointerHold(true);
      }}
      onPointerLeave={(event) => {
        if (leftGroup(event)) setPointerHold(false);
      }}
      onFocusIn={() => {
        if (open) setFocusHold(true);
      }}
      onFocusOut={(event) => {
        if (leftGroup(event)) setFocusHold(false);
      }}
    >
      <button
        type="button"
        class={`response-status-toggle${open ? " is-open" : ""}${outcome === "Finished with issues" ? " is-warned" : ""}`}
        hidden={!toggleVisible}
        aria-expanded={toggleVisible ? open : undefined}
        aria-controls={listId}
        onClick={() => {
          // A manual collapse overrides any inspection hold still pinning rows open.
          if (open) {
            setExpanded(false);
            setPointerHold(false);
            setFocusHold(false);
          } else {
            setExpanded(true);
            setPointerHold(groupRef.current?.matches(":hover") ?? false);
            setFocusHold(groupRef.current?.contains(document.activeElement) ?? false);
          }
        }}
      >
        <Icon name="chevron" />
        <span class="response-status-kind">Preparation</span>
        <span aria-hidden="true" class="response-status-separator">
          ·
        </span>
        <span class="response-status-value">{outcome}</span>
      </button>
      {usedSessionModel && (
        <p class="response-status-session-model">
          Used the chat model for preparation
          {context.onChooseModels && (
            <button type="button" onClick={context.onChooseModels}>
              Choose models
            </button>
          )}
        </p>
      )}
      <div class="response-status-list" id={listId}>
        {open
          ? rows.map((summary) => (
              <PrepassRow
                key={`${summary.boundaryId}/${summary.category}`}
                summary={summary}
                context={context}
                historyRevision={historyRevision}
              />
            ))
          : null}
      </div>
    </div>
  );
}

function PrepassRow({
  summary,
  context,
  historyRevision,
}: {
  summary: PrepassSummary;
  context: ResponseStatusContext;
  historyRevision: number;
}) {
  const row = prepassRowModel(summary);
  return (
    <Tooltip
      content={
        <PrepassDetailsView
          client={context.client}
          sessionId={context.sessionId}
          boundaryId={row.boundaryId}
          category={row.category}
          historyRevision={historyRevision}
          modelNames={context.modelNames}
          summaryError={summary.error}
        />
      }
      placement="below"
      dismissOnClick={false}
      disabled={!context.visible}
    >
      <span
        class={`response-status-row${row.mood === "warned" ? " is-warned" : ""}`}
        tabIndex={0}
        onKeyDown={scrollTooltipDetails}
      >
        <RowStatusMood mood={row.mood} />
        <span class="response-status-kind">{row.label}</span>
        <span aria-hidden="true" class="response-status-separator">
          ·
        </span>
        <span class="response-status-value">
          {row.value}
          {summary.error ? ` · Warning: ${summary.error}` : ""}
        </span>
      </span>
    </Tooltip>
  );
}

// Row icons are decorative; the status text carries the state aloud.
function RowStatusMood({ mood }: { mood: PrepassRowModel["mood"] }) {
  if (mood === "settled") {
    return (
      <svg
        class="response-status-icon"
        viewBox="0 0 16 16"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.8"
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
      >
        <path d="m3 8.2 3.1 3.1L13 4.6" />
      </svg>
    );
  }
  if (mood === "stopped") {
    return (
      <svg
        class="response-status-icon"
        viewBox="0 0 16 16"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.8"
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
      >
        <path d="M6 6l4 4m0-4-4 4" />
      </svg>
    );
  }
  if (mood === "warned") {
    return (
      <svg
        class="response-status-icon"
        viewBox="0 0 16 16"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.7"
        strokeLinecap="round"
        strokeLinejoin="round"
        aria-hidden="true"
      >
        <path d="M8 3.3 14 12.9H2Z" />
        <path d="M8 7.4v2.6m0 1.6v.01" />
      </svg>
    );
  }
  return (
    <svg
      class={`response-status-icon${mood === "running" ? " is-running" : ""}`}
      viewBox="0 0 16 16"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.8"
      strokeLinecap="round"
      aria-hidden="true"
    >
      <circle cx="8" cy="8" r="5.5" stroke-dasharray={mood === "running" ? "22 12" : "0 34"} />
    </svg>
  );
}

// Response header appears after user text or before the first response entry; a compaction
// marker keeps its preparation rows after the marker and its header before it.
export function boundaryResponseStatus(
  kind: TranscriptEntryKind,
  response: ResponseHeaderInfo | null | undefined,
  preparation: readonly PrepassSummary[] | null | undefined,
  context: ResponseStatusContext,
  historyRevision: number,
): { before: ComponentChildren; after: ComponentChildren } {
  if (kind === "compaction") {
    return {
      before: response ? (
        <div class="response-status">
          <ResponseHeader response={response} modelNames={context.modelNames} />
        </div>
      ) : null,
      after: preparation?.length ? (
        <PrepassRows
          preparation={preparation}
          context={context}
          historyRevision={historyRevision}
        />
      ) : null,
    };
  }
  const status =
    response || preparation?.length ? (
      <div class="response-status">
        {response ? <ResponseHeader response={response} modelNames={context.modelNames} /> : null}
        {preparation?.length ? (
          <PrepassRows
            preparation={preparation}
            context={context}
            historyRevision={historyRevision}
          />
        ) : null}
      </div>
    ) : null;
  return kind === "user" ? { before: null, after: status } : { before: status, after: null };
}

// A pre-send response has no transcript anchor yet; it renders transiently at the transcript tail.
export function PendingResponseStatus({
  header,
  preparation,
  context,
  historyRevision,
}: {
  header: ResponseHeaderInfo;
  preparation: readonly PrepassSummary[];
  context: ResponseStatusContext;
  historyRevision: number;
}) {
  return (
    <div class="response-status">
      <ResponseHeader response={header} modelNames={context.modelNames} />
      {preparation.length > 0 && (
        <PrepassRows
          preparation={preparation}
          context={context}
          historyRevision={historyRevision}
        />
      )}
    </div>
  );
}
