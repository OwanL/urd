import { useEffect, useState } from "preact/hooks";
import type { PromptCacheSummary, PromptCacheTotals } from "../../usage/Usage.ts";
import { UsageClient } from "../../usage/UsageClient.ts";
import { promptCacheText } from "./PromptCachePresentation.ts";

// Tooltip mounts this content only while open.
export function PromptCacheDetails({
  sessionId,
  refreshKey,
}: {
  sessionId: string;
  refreshKey: string | number;
}) {
  const [summary, setSummary] = useState<PromptCacheSummary | null>(null);
  const [failed, setFailed] = useState(false);
  useEffect(() => {
    const request = new AbortController();
    const client = new UsageClient(new URL(window.location.href));
    async function read() {
      try {
        const next = await client.getPromptCache(sessionId, request.signal);
        if (request.signal.aborted) return;
        setSummary(next);
        setFailed(false);
      } catch {
        if (!request.signal.aborted) setFailed(true);
      }
    }
    void read();
    return () => request.abort();
  }, [sessionId, refreshKey]);

  return (
    <div class="prompt-cache-details">
      <span>Cache</span>
      {summary && (
        <dl class="prompt-cache-rows">
          <PromptCacheRow label="Last turn" totals={summary.lastTurn} />
          <PromptCacheRow label="Session" totals={summary.session} />
        </dl>
      )}
      {failed ? (
        <span class="tooltip-detail">{summary ? "Stale · refresh failed" : "Unavailable"}</span>
      ) : (
        !summary && <span class="tooltip-detail">Loading…</span>
      )}
    </div>
  );
}

function PromptCacheRow({ label, totals }: { label: string; totals: PromptCacheTotals | null }) {
  return (
    <div class="prompt-cache-row">
      <dt>{label}</dt>
      <dd>{promptCacheText(totals)}</dd>
    </div>
  );
}
