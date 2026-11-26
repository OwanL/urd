import { useId, useMemo, useRef, useState } from "preact/hooks";
import type { UsageRange, UsageSummary } from "./Usage.ts";
import { UsageClient } from "./UsageClient.ts";
import { UsageComposition } from "./UsageComposition.tsx";
import { UsageHistory } from "./UsageHistory.tsx";
import { UsageProviderBreakdown, UsageProviders } from "./UsageProviders.tsx";
import { useUsageSummary } from "./useUsageSummary.ts";
import { cumulativeUsageHistory, usageProviders, usageSources } from "./UsageAggregation.ts";
import {
  formatSummaryCost,
  formatUsageCost,
  formatUsageCount,
  usageColors,
} from "./UsagePresentation.ts";

const costScopes = [
  { value: "session", label: "Session" },
  { value: "day", label: "24h" },
  { value: "sevenDays", label: "7d" },
] as const;

interface UsageDetailsProps {
  client: UsageClient;
  sessionId?: string;
  range?: UsageRange;
}

export function UsageDetails({ client, sessionId, range }: UsageDetailsProps) {
  const id = useId();
  const [scope, setScope] = useState<"session" | UsageRange>(
    sessionId ? "session" : (range ?? "day"),
  );
  return (
    <section class="usage-details" aria-label="Usage details">
      {sessionId && (
        <div class="usage-tabs" role="tablist" aria-label="Cost scope">
          {costScopes.map((tab, index) => (
            <button
              key={tab.value}
              id={`${id}-${tab.value}`}
              type="button"
              role="tab"
              aria-selected={scope === tab.value}
              aria-controls={`${id}-panel`}
              tabIndex={scope === tab.value ? 0 : -1}
              onClick={() => setScope(tab.value)}
              onKeyDown={(event) => {
                let next: number;
                if (event.key === "ArrowRight") next = (index + 1) % costScopes.length;
                else if (event.key === "ArrowLeft")
                  next = (index + costScopes.length - 1) % costScopes.length;
                else if (event.key === "Home") next = 0;
                else if (event.key === "End") next = costScopes.length - 1;
                else return;
                event.preventDefault();
                const target = costScopes[next]!;
                setScope(target.value);
                document.getElementById(`${id}-${target.value}`)?.focus();
              }}
            >
              {tab.label}
            </button>
          ))}
        </div>
      )}
      <div
        id={`${id}-panel`}
        role={sessionId ? "tabpanel" : undefined}
        aria-labelledby={sessionId ? `${id}-${scope}` : undefined}
        tabIndex={sessionId ? 0 : undefined}
      >
        <UsageSnapshot
          key={scope}
          client={client}
          {...(scope === "session" ? { sessionId: sessionId! } : { range: scope })}
        />
      </div>
    </section>
  );
}

function UsageSnapshot({ client, sessionId, range }: UsageDetailsProps) {
  // Each scope is read on demand and stays still until switched or reopened.
  const snapshot = useRef<UsageSummary | null>(null);
  const state = useUsageSummary({
    client,
    enabled: snapshot.current === null,
    pollMilliseconds: null,
    refreshKey: 0,
    ...(sessionId ? { sessionId } : { range: range ?? "day" }),
    detail: true,
  });
  snapshot.current ??= state.summary;
  const summary = snapshot.current;
  return (
    <>
      <header class="usage-row usage-heading">
        <h2>
          {sessionId ? "Session cost" : range === "sevenDays" ? "7d API cost" : "24h API cost"}
        </h2>
        <strong>{summary ? formatSummaryCost(summary) : "…"}</strong>
      </header>
      {state.stale && (
        <p class="usage-warning" role="status">
          {state.summary ? "Stale totals · refresh failed" : "Usage unavailable"}
        </p>
      )}
      {state.loading && <p class="usage-muted">Loading…</p>}
      {summary && (
        <SummaryContent
          summary={summary}
          session={!!sessionId}
          dateLabels={range === "sevenDays"}
        />
      )}
    </>
  );
}

function SummaryContent({
  summary,
  session,
  dateLabels,
}: {
  summary: UsageSummary;
  session: boolean;
  dateLabels: boolean;
}) {
  const [selected, setSelected] = useState<number | null>(null);
  const history = useMemo(() => cumulativeUsageHistory(summary.history), [summary.history]);
  const providers = usageProviders(summary.breakdown);
  const sources = usageSources(summary.breakdown);
  const sourceColors = usageColors(sources.map((source) => source.key));
  const totals = summary.totals;
  // A partly overlapping closed-session range has no retained totals; a zero would be false.
  if (summary.totalsComplete === false) {
    return <p class="usage-note">Totals unavailable: closed sessions keep only lifetime totals.</p>;
  }
  return (
    <>
      {session && (
        <p class="usage-muted">
          {formatUsageCount(totals.requests)} requests
          {totals.activeRequests > 0 ? ` · ${formatUsageCount(totals.activeRequests)} active` : ""}
        </p>
      )}
      {session ? (
        <UsageComposition items={providers} label="Cost by provider" />
      ) : (
        <UsageHistory
          history={history}
          dateLabels={dateLabels}
          selected={selected}
          onSelect={setSelected}
        />
      )}
      {session ? (
        <UsageProviderBreakdown providers={providers} />
      ) : (
        <UsageProviders providers={providers} onInspect={() => setSelected(null)} />
      )}
      {session && sources.length > 1 && (
        <section class="usage-sources">
          <h3>Cost sources</h3>
          <UsageComposition items={sources} label="Cost by source" />
          <div class="usage-source-list">
            {sources.map((source) => (
              <div class="usage-row" key={source.key}>
                <span class="usage-swatch" style={{ background: sourceColors[source.key] }} />
                <span>{source.label}</span>
                <span class="usage-muted">{formatUsageCount(source.totals.requests)} req</span>
                <span>{formatUsageCost(source.totals)}</span>
              </div>
            ))}
          </div>
        </section>
      )}
      {totals.unknownRequests > 0 && (
        <p class="usage-note">Totals may exclude unpriced requests.</p>
      )}
    </>
  );
}
