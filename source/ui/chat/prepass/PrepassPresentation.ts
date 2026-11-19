import { initialBoundaryId } from "../../sessions/transcript/Prepass.ts";
import type { PrepassSummary } from "../../sessions/transcript/Prepass.ts";
import type { TranscriptEntryDescriptor } from "../../sessions/transcript/Messages.ts";

const categoryOrder = ["tools", "skills", "instructions", "title"] as const;

export interface PrepassRowModel {
  readonly category: string;
  readonly boundaryId: string;
  readonly label: string;
  readonly value: string;
  readonly mood: "settled" | "warned" | "running" | "pending" | "stopped";
}

const labels: Record<string, string> = {
  tools: "Tools",
  skills: "Skills",
  instructions: "Instructions",
  title: "Title",
};

export function unanchoredPrepass(
  pending: readonly PrepassSummary[],
  entries: readonly TranscriptEntryDescriptor[],
  historyRevision: number,
): readonly PrepassSummary[] {
  // A marker in the displayed window owns its preparation; otherwise keep the live feedback.
  return pending.filter(
    (row) =>
      !entries.some(
        ({ entry, preparation }) =>
          entry.kind === "compaction" &&
          entry.historyRevision === historyRevision &&
          entry.entryId === row.boundaryId &&
          preparation?.some(
            (anchored) =>
              anchored.boundaryId === row.boundaryId && anchored.category === row.category,
          ),
      ),
  );
}

// Preparation rows keep their established category order; unknown categories stay truthful.
export function orderedPrepass(
  preparation: readonly PrepassSummary[] | null | undefined,
): PrepassSummary[] {
  if (!preparation?.length) return [];
  const rank = (category: string): number => {
    const index = categoryOrder.indexOf(category as (typeof categoryOrder)[number]);
    return index === -1 ? categoryOrder.length : index;
  };
  return [...preparation].sort(
    (left, right) =>
      rank(left.category) - rank(right.category) || left.category.localeCompare(right.category),
  );
}

// Unknown states stay expanded rather than being mistaken for completed work.
export function prepassOutcome(
  preparation: readonly PrepassSummary[],
): "Complete" | "Finished with issues" | null {
  const terminal = ["completed", "fallback", "cancelled", "interrupted", "failed"];
  if (!preparation.length || preparation.some((row) => !terminal.includes(row.state))) return null;
  return preparation.every((row) => row.state === "completed" && !row.error)
    ? "Complete"
    : "Finished with issues";
}

export function prepassRowMood(summary: PrepassSummary): PrepassRowModel["mood"] {
  if (summary.error && (summary.state === "completed" || summary.state === "running"))
    return "warned";
  if (summary.state === "running") return "running";
  if (summary.state === "pending") return "pending";
  if (summary.state === "completed") return "settled";
  if (summary.state === "fallback") return "warned";
  return "stopped";
}

export function prepassRowModel(summary: PrepassSummary): PrepassRowModel {
  return {
    category: summary.category,
    boundaryId: summary.boundaryId,
    label: labels[summary.category] ?? summarizeState(summary.category),
    value: prepassRowValue(summary),
    mood: prepassRowMood(summary),
  };
}

function prepassRowValue(summary: PrepassSummary): string {
  switch (summary.state) {
    case "running":
      // A known candidate count is not fabricated; absent counts stay unspecific.
      if (summary.category === "title") return "Generating";
      return summary.candidateCount !== null && summary.candidateCount > 0
        ? summary.candidateCount === 1
          ? "Reviewing 1 candidate"
          : `Reviewing ${summary.candidateCount} candidates`
        : "Running";
    case "pending":
      return "Pending";
    case "fallback":
      if (summary.candidateCount !== null && summary.resultCount !== null)
        return `Failed · retained ${summary.resultCount}/${summary.candidateCount}`;
      if (summary.category === "tools") return "Failed · kept all";
      if (summary.category === "skills" || summary.category === "instructions")
        return "Failed · protected only";
      return "Failed";
    case "cancelled":
      return "Cancelled";
    case "interrupted":
      return "Interrupted";
    case "completed":
      if (summary.category === "title") return "Complete";
      return summary.candidateCount === null || summary.resultCount === null
        ? "Completed"
        : `${summary.candidateCount - summary.resultCount}/${summary.candidateCount} pruned`;
    default:
      return summarizeState(summary.state);
  }
}

function summarizeState(state: string): string {
  return state ? `${state[0]!.toUpperCase()}${state.slice(1)}` : "Unknown";
}

// The chat-model fallback only warrants a warning on the initial boundary; compaction
// boundaries carry their own history and never re-run the first-message preparation.
export function prepassUsedSessionModel(
  preparation: readonly PrepassSummary[] | null | undefined,
): boolean {
  return (
    preparation?.some(
      (summary) => summary.usedSessionModel === true && summary.boundaryId === initialBoundaryId,
    ) ?? false
  );
}

// The catalog cache supplies friendly names; uncached historical IDs stay opaque, never guessed.
export function uniqueModelNames(
  options: readonly { modelId: string; modelLabel: string }[],
): ReadonlyMap<string, string> {
  const names = new Map<string, string>();
  const ambiguous = new Set<string>();
  for (const option of options) {
    if (ambiguous.has(option.modelId)) continue;
    const known = names.get(option.modelId);
    if (known === undefined) names.set(option.modelId, option.modelLabel);
    else if (known !== option.modelLabel) {
      names.delete(option.modelId);
      ambiguous.add(option.modelId);
    }
    // An ambiguous name is removed and stays removed; the raw model ID is used instead.
  }
  return names;
}

export function modelDisplayName(
  modelId: string | null | undefined,
  names: ReadonlyMap<string, string> | null,
): string | null {
  if (!modelId) return null;
  return names?.get(modelId) ?? modelId;
}

// Reasoning effort passes through verbatim; absence is shown by omission, never invented.
export function effortLabel(effort: string | null | undefined): string | null {
  return effort ? effort : null;
}
