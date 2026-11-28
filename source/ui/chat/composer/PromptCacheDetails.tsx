import type { PromptCacheTotals } from "../../usage/Usage.ts";
import type { PromptCacheState } from "./usePromptCache.ts";
import { promptCacheText } from "./PromptCachePresentation.ts";

export function PromptCacheDetails({
  summary,
  failed,
  showTitle = true,
}: PromptCacheState & { showTitle?: boolean }) {
  return (
    <div class="prompt-cache-details">
      {showTitle && <span>Cache</span>}
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
