import { useCallback, useRef } from "preact/hooks";
import { IconButton } from "../../controls/IconButton.tsx";
import { ComposerControls } from "../composer/ComposerControls.tsx";
import { effortLabel } from "../composer/ReasoningPicker.tsx";
import { usePromptCache } from "../composer/usePromptCache.ts";
import { SessionIndicators } from "../composer/SessionIndicators.tsx";
import type { SubagentViewSnapshot } from "../../sessions/SessionSnapshot.ts";

export function SubagentComposer({
  sessionId,
  snapshot,
  active,
}: {
  sessionId: string;
  snapshot: SubagentViewSnapshot | null;
  active: boolean;
}) {
  const panel = useRef<HTMLElement>(null);
  const focusPanel = useCallback(() => panel.current?.focus({ preventScroll: true }), []);
  const refresh = snapshot?.live ? "live" : "settled";
  const measuredContext =
    snapshot?.contextUsage?.source === "measured" ? snapshot.contextUsage.usedTokens : "";
  const promptCache = usePromptCache(
    sessionId,
    active,
    `${refresh}:${snapshot?.historyRevision ?? ""}:${measuredContext}`,
  );
  const model = snapshot?.modelId;
  const effort = snapshot?.reasoningEffort;
  return (
    <section ref={panel} tabIndex={-1} class="composer" aria-label="Subagent session details">
      <textarea
        class="composer-input"
        aria-label="Message"
        placeholder="Subagent is read-only"
        rows={1}
        disabled
      />
      <ComposerControls
        selectionLayoutKey={`${model}:${effort}`}
        focusFallback={focusPanel}
        selections={
          <>
            {model && (
              <span class="subagent-model-label" title={model}>
                {model}
              </span>
            )}
            {effort && (
              <span class="subagent-effort-label" title="Reasoning effort">
                {effortLabel(effort)}
              </span>
            )}
          </>
        }
        actions={<IconButton icon="send" label="Send" class="composer-send" disabled />}
        indicators={
          <SessionIndicators
            contextUsage={snapshot?.contextUsage ?? null}
            contextWindow={snapshot?.contextWindow ?? null}
            workingTime={snapshot?.workingTime ?? null}
            active={active}
            fenced={snapshot?.admissionFenced ?? true}
            sessionId={sessionId}
            usageActive={active}
            usageBusy={snapshot?.live ?? false}
            usageRefresh={refresh}
            promptCache={promptCache}
          />
        }
      />
    </section>
  );
}
