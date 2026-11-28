import { useId, useRef, useState } from "preact/hooks";
import { Popover } from "../../controls/Popover.tsx";
import type { PromptCacheTotals } from "../../usage/Usage.ts";
import { PromptCacheDetails } from "./PromptCacheDetails.tsx";
import { formatCacheShare } from "./PromptCachePresentation.ts";
import type { PromptCacheState } from "./usePromptCache.ts";

const label = "Low prompt cache reuse";

export function PromptCacheWarning({
  warning,
  cache,
}: {
  warning: PromptCacheTotals;
  cache: PromptCacheState;
}) {
  const [open, setOpen] = useState(false);
  const anchor = useRef<HTMLButtonElement>(null);
  const id = useId();
  return (
    <>
      <button
        ref={anchor}
        type="button"
        class="prompt-cache-warning-button"
        aria-label={label}
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-controls={open ? id : undefined}
        onClick={() => setOpen((current) => !current)}
      >
        <WarningIcon />
      </button>
      {open && (
        <Popover
          id={id}
          anchor={anchor}
          label={label}
          class="prompt-cache-warning-popover"
          placement="above"
          onClose={() => setOpen(false)}
        >
          <div class="prompt-cache-warning-title">
            <WarningIcon /> {label}
          </div>
          <div>
            <strong>
              {formatCacheShare(warning.cachedInputTokens, warning.inputTokens)} cached
            </strong>{" "}
            after the first request
          </div>
          <div class="prompt-cache-warning-coverage">
            {warning.reportedRequests} measured requests · warning below 60%
          </div>
          <PromptCacheDetails {...cache} showTitle={false} />
        </Popover>
      )}
    </>
  );
}

function WarningIcon() {
  return (
    <svg
      width="14"
      height="14"
      viewBox="0 0 16 16"
      fill="none"
      stroke="currentColor"
      stroke-width="1.3"
      stroke-linecap="round"
      stroke-linejoin="round"
      aria-hidden="true"
    >
      <path d="M7.1 2.6a1 1 0 0 1 1.8 0l5.45 9.55a1 1 0 0 1-.87 1.5H2.52a1 1 0 0 1-.87-1.5ZM8 6v3m0 2.3v.1" />
    </svg>
  );
}
