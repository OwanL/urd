import { useState } from "preact/hooks";
import { useUsageSummary } from "./useUsageSummary.ts";
import type { UsageRange, UsageRequest, UsageSummary } from "./UsageTypes.ts";
import { UsageClient } from "./UsageClient.ts";
import { UsageHistory } from "./UsageHistory.tsx";
import {
  cumulativeUsageHistory,
  formatCostUsd,
  formatUsageCost,
  formatUsageCount,
  formatUsageTime,
} from "./UsageValues.ts";

export function UsageDetails({
  client,
  label,
  sessionId,
  range,
  active,
  busy,
  refreshKey,
}: {
  client: UsageClient;
  label: string;
  sessionId?: string;
  range?: UsageRange;
  active: boolean;
  busy: boolean;
  refreshKey: number | string;
}) {
  const state = useUsageSummary({
    client,
    enabled: active,
    pollMilliseconds: sessionId ? (busy ? 1_000 : null) : 15_000,
    refreshKey,
    ...(sessionId ? { sessionId } : { range: range ?? "today" }),
    detail: true,
  });
  return (
    <section class="usage-details" aria-label="Usage details">
      <header class="usage-row usage-heading">
        <h2>{sessionId ? "Session cost" : label}</h2>
        <strong>{state.summary ? formatUsageCost(state.summary.totals) : "…"}</strong>
      </header>
      {state.stale && (
        <p class="usage-warning" role="status">
          {state.summary ? "Stale totals · refresh failed" : "Usage unavailable"}
        </p>
      )}
      {state.loading && <p class="usage-muted">Loading usage…</p>}
      {state.summary && <SummaryContent summary={state.summary} />}
    </section>
  );
}

function SummaryContent({ summary }: { summary: UsageSummary }) {
  const [expanded, setExpanded] = useState(false);
  const totals = summary.totals;
  const history = cumulativeUsageHistory(summary.history);
  return (
    <>
      <p class="usage-row usage-muted">
        <span>{formatUsageCount(totals.requests)} requests · API-equivalent USD</span>
        {totals.activeRequests > 0 ? (
          <span>{formatUsageCount(totals.activeRequests)} active</span>
        ) : totals.estimatedRequests > 0 ? (
          <span>Provisional</span>
        ) : null}
      </p>
      {history.length > 0 && <UsageHistory history={history} />}
      {totals.requests > 0 && (
        <details
          class="usage-inspection"
          onToggle={(event) => setExpanded(event.currentTarget.open)}
        >
          <summary>Breakdown and requests</summary>
          {expanded && (
            <>
              {summary.breakdown.length > 0 && (
                <ul class="usage-list" aria-label="Provider, model and purpose breakdown">
                  {summary.breakdown.map((item, index) => (
                    <li
                      class="usage-row"
                      key={`${item.provider}/${item.model}/${item.purpose}/${index}`}
                    >
                      <span class="usage-name">
                        <strong>{item.model}</strong>
                        <span>
                          {item.provider} · {item.purpose}
                        </span>
                      </span>
                      <span class="usage-amount">{formatUsageCost(item.totals)}</span>
                    </li>
                  ))}
                </ul>
              )}
              {summary.recent.length > 0 && (
                <section class="usage-section">
                  <h3>Recent requests</h3>
                  {summary.recent.map((request) => (
                    <RecentRequest key={request.id} request={request} />
                  ))}
                </section>
              )}
              {history.length > 0 && (
                <details class="usage-section">
                  <summary>History values</summary>
                  <table class="usage-table">
                    <caption>Cumulative cost</caption>
                    <thead>
                      <tr>
                        <th scope="col">Time</th>
                        <th scope="col">Cost</th>
                        <th scope="col">Requests</th>
                      </tr>
                    </thead>
                    <tbody>
                      {history.map((point) => (
                        <tr key={point.at}>
                          <td>{formatUsageTime(point.at)}</td>
                          <td>{formatUsageCost(point.totals)}</td>
                          <td>{formatUsageCount(point.totals.requests)}</td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </details>
              )}
            </>
          )}
        </details>
      )}
    </>
  );
}

function RecentRequest({ request }: { request: UsageRequest }) {
  const cost = request.costUsd === null ? "Unknown" : formatCostUsd(request.costUsd);
  return (
    <details class="usage-request">
      <summary>
        <span class="usage-name">
          <strong>{request.model}</strong>
          <span>
            {request.purpose} · {formatUsageTime(request.startedAt)}
          </span>
        </span>
        <span class="usage-amount">
          {request.isEstimate && request.costUsd !== null ? `~${cost}` : cost}
        </span>
      </summary>
      {request.problem && <p class="usage-warning">{request.problem}</p>}
      <dl class="usage-metadata">
        <dt>Provider</dt>
        <dd>{request.provider}</dd>
        {request.tokens && (
          <>
            <dt>Input / output</dt>
            <dd>
              {count(request.tokens.inputTokens)} / {count(request.tokens.outputTokens)}
            </dd>
            <dt>Cached input</dt>
            <dd>{count(request.tokens.cachedInputTokens)}</dd>
            {request.tokens.reasoningTokens != null && (
              <>
                <dt>Reasoning (in output)</dt>
                <dd>{count(request.tokens.reasoningTokens)}</dd>
              </>
            )}
            {(request.tokens.cacheWrite30mTokens !== 0 ||
              request.tokens.cacheWrite12hTokens !== 0) && (
              <>
                <dt>Cache writes · 30m / 12h</dt>
                <dd>
                  {count(request.tokens.cacheWrite30mTokens)} /{" "}
                  {count(request.tokens.cacheWrite12hTokens)}
                </dd>
              </>
            )}
          </>
        )}
        {request.rates && (
          <>
            <dt>Pricing source</dt>
            <dd>{request.rates.source}</dd>
            <dt>Rates captured</dt>
            <dd>{formatUsageTime(request.rates.capturedAt)}</dd>
          </>
        )}
      </dl>
    </details>
  );
}

function count(value: number | null): string {
  return value === null ? "Unknown" : formatUsageCount(value);
}
