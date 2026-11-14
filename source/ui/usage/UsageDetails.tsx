import { useUsageSummary } from "./useUsageSummary.ts";
import type { UsageBreakdown, UsageRange, UsageRequest, UsageSummary } from "./UsageTypes.ts";
import { UsageClient } from "./UsageClient.ts";
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
      <header class="usage-details-header">
        <div>
          <h2>{sessionId ? "Session" : label}</h2>
          <p>API-equivalent USD · not invoices</p>
        </div>
        {state.summary && <strong>{formatUsageCost(state.summary.totals)}</strong>}
      </header>
      {state.loading && <p class="usage-message">Loading usage…</p>}
      {state.stale && (
        <p class="usage-message is-stale" role="status">
          {state.summary ? "Refresh failed. Showing stale totals." : "Could not load usage."}
        </p>
      )}
      {state.summary && <SummaryContent summary={state.summary} />}
    </section>
  );
}

function SummaryContent({ summary }: { summary: UsageSummary }) {
  const totals = summary.totals;
  const history = cumulativeUsageHistory(summary.history);
  return (
    <>
      <dl class="usage-total-facts">
        <div>
          <dt>Requests</dt>
          <dd>{formatUsageCount(totals.requests)}</dd>
        </div>
        {totals.estimatedRequests > 0 && (
          <div>
            <dt>Provisional estimates</dt>
            <dd>{formatUsageCount(totals.estimatedRequests)}</dd>
          </div>
        )}
        {totals.unknownRequests > 0 && (
          <div>
            <dt>Incomplete</dt>
            <dd>{formatUsageCount(totals.unknownRequests)}</dd>
          </div>
        )}
        {totals.activeRequests > 0 && (
          <div>
            <dt>Active</dt>
            <dd>{formatUsageCount(totals.activeRequests)}</dd>
          </div>
        )}
      </dl>
      {totals.estimatedRequests > 0 && (
        <p class="usage-estimate-note">Provisional; provider usage is not final.</p>
      )}
      <UsageHistory history={history} />
      <section class="usage-detail-section">
        <h3>By provider, model and purpose</h3>
        {summary.breakdown.length ? (
          <ul class="usage-breakdown">
            {summary.breakdown.map((item, index) => (
              <BreakdownRow
                key={`${item.provider}/${item.model}/${item.purpose}/${index}`}
                item={item}
              />
            ))}
          </ul>
        ) : (
          <p class="usage-message">No breakdown available.</p>
        )}
      </section>
      <section class="usage-detail-section">
        <h3>Recent requests</h3>
        {summary.recent.length ? (
          <ol class="usage-recent">
            {summary.recent.map((request) => (
              <RecentRequest key={request.id} request={request} />
            ))}
          </ol>
        ) : (
          <p class="usage-message">No recent requests.</p>
        )}
      </section>
      {totals.unknownRequests > 0 && (
        <p class="usage-footnote">Incomplete costs are excluded or estimated in the subtotal.</p>
      )}
    </>
  );
}

function UsageHistory({ history }: { history: ReturnType<typeof cumulativeUsageHistory> }) {
  if (history.length === 0) return null;
  const width = 320;
  const height = 54;
  const max = Math.max(...history.map((point) => point.totals.costUsd), 0);
  const coordinates = history.map((point, index) => ({
    x: history.length === 1 ? width / 2 : (index / (history.length - 1)) * width,
    y: height - (max > 0 ? (point.totals.costUsd / max) * height : 0),
  }));
  const points = coordinates.map(({ x, y }) => `${x.toFixed(1)},${y.toFixed(1)}`);
  return (
    <section class="usage-detail-section usage-history" aria-label="Cumulative cost history">
      <h3>Cumulative cost</h3>
      <svg viewBox={`0 0 ${width} ${height}`} role="img" aria-label={historyLabel(history)}>
        <polyline points={points.join(" ")} />
        {coordinates.map(({ x, y }, index) => (
          <circle key={history[index]!.at} cx={x} cy={y} r="2.5" />
        ))}
      </svg>
      <details>
        <summary>View cumulative values</summary>
        <table>
          <caption>Cumulative API-equivalent cost</caption>
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
    </section>
  );
}

function historyLabel(history: ReturnType<typeof cumulativeUsageHistory>): string {
  const first = history[0];
  const last = history.at(-1);
  return first && last
    ? `Cumulative API-equivalent cost from ${formatUsageCost(first.totals)} to ${formatUsageCost(last.totals)} across ${history.length} intervals.`
    : "No usage history.";
}

function BreakdownRow({ item }: { item: UsageBreakdown }) {
  return (
    <li>
      <div class="usage-breakdown-name">
        <strong>{item.model}</strong>
        <span>
          {item.provider} · {item.purpose}
        </span>
      </div>
      <div class="usage-breakdown-total">
        <strong>{formatUsageCost(item.totals)}</strong>
        <span>{formatUsageCount(item.totals.requests)} requests</span>
      </div>
    </li>
  );
}

function RecentRequest({ request }: { request: UsageRequest }) {
  const cost = request.costUsd === null ? "Unknown" : formatCostUsd(request.costUsd);
  const shownCost = request.isEstimate && request.costUsd !== null ? `~${cost}` : cost;
  return (
    <li>
      <div class="usage-recent-heading">
        <div>
          <strong>{request.model}</strong>
          <span>
            {request.provider} · {request.purpose}
          </span>
        </div>
        <strong>{shownCost}</strong>
      </div>
      <time dateTime={request.startedAt}>{formatUsageTime(request.startedAt)}</time>
      {request.isEstimate && <span class="usage-estimate-tag">Provisional</span>}
      {request.problem && <p class="usage-request-problem">{request.problem}</p>}
      {(request.tokens || request.rates) && (
        <details class="usage-request-metadata">
          <summary>Tokens and rates</summary>
          <dl>
            {request.tokens && (
              <>
                <dt>Input / output</dt>
                <dd>
                  {formatNullableCount(request.tokens.inputTokens)} /{" "}
                  {formatNullableCount(request.tokens.outputTokens)}
                </dd>
                {request.tokens.reasoningTokens != null && (
                  <>
                    <dt>Reasoning (included in output)</dt>
                    <dd>{formatNullableCount(request.tokens.reasoningTokens)}</dd>
                  </>
                )}
                <dt>Cached input</dt>
                <dd>{formatNullableCount(request.tokens.cachedInputTokens)}</dd>
                <dt>Cache writes (30m / 12h)</dt>
                <dd>
                  {formatNullableCount(request.tokens.cacheWrite30mTokens)} /{" "}
                  {formatNullableCount(request.tokens.cacheWrite12hTokens)}
                </dd>
              </>
            )}
            {request.rates && (
              <>
                <dt>Rate source</dt>
                <dd>
                  {request.rates.source} · {request.rates.model}
                </dd>
                <dt>Rates captured</dt>
                <dd>{formatUsageTime(request.rates.capturedAt)}</dd>
              </>
            )}
          </dl>
        </details>
      )}
    </li>
  );
}

function formatNullableCount(value: number | null): string {
  return value === null ? "Unknown" : formatUsageCount(value);
}
