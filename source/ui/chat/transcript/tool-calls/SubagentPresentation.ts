import type { TranscriptEntryDescriptor } from "../../../sessions/transcript/Messages.ts";

export type SubagentRowPresentation =
  | {
      readonly kind: "call";
      readonly heading: "Subagent" | "Subagents" | null;
      readonly hasCompletion: boolean;
    }
  | { readonly kind: "completion"; readonly heading: "Subagent"; readonly hasCompletion: true }
  | { readonly kind: "hidden" };

interface RankedDescriptor {
  readonly descriptor: TranscriptEntryDescriptor;
  readonly rank: number;
}

// Evaluate the loaded window, not the mounted/overscan slice, so ordinary scrolling
// doesn't repeat headings or move the report between call and completion rows.
export function subagentPresentation(
  rows: readonly RankedDescriptor[],
): ReadonlyMap<number, SubagentRowPresentation> {
  const calls = new Set<string>();
  const completions = new Set<string>();
  const keys = new Map<number, string>();
  for (const { descriptor, rank } of rows) {
    const { entry, tool } = descriptor;
    if (tool?.name !== "subagent" || (entry.kind !== "tool" && entry.kind !== "toolCompletion"))
      continue;
    const key = JSON.stringify([
      entry.historyRevision,
      entry.executionId,
      tool.requestId,
      tool.callId,
    ]);
    keys.set(rank, key);
    (entry.kind === "tool" ? calls : completions).add(key);
  }

  const result = new Map<number, SubagentRowPresentation>();
  for (const [index, row] of rows.entries()) {
    const key = keys.get(row.rank);
    if (key === undefined) continue;
    if (row.descriptor.entry.kind === "toolCompletion") {
      result.set(
        row.rank,
        calls.has(key)
          ? { kind: "hidden" }
          : {
              kind: "completion",
              heading: "Subagent",
              hasCompletion: true,
            },
      );
      continue;
    }
    result.set(row.rank, {
      kind: "call",
      heading: adjacentSiblings(rows[index - 1], row)
        ? null
        : adjacentSiblings(row, rows[index + 1])
          ? "Subagents"
          : "Subagent",
      hasCompletion: completions.has(key),
    });
  }
  return result;
}

function adjacentSiblings(
  left: RankedDescriptor | undefined,
  right: RankedDescriptor | undefined,
): boolean {
  return Boolean(
    left &&
    right &&
    left.rank + 1 === right.rank &&
    left.descriptor.entry.kind === "tool" &&
    right.descriptor.entry.kind === "tool" &&
    left.descriptor.tool?.name === "subagent" &&
    right.descriptor.tool?.name === "subagent" &&
    left.descriptor.tool.requestId === right.descriptor.tool.requestId &&
    left.descriptor.entry.executionId === right.descriptor.entry.executionId &&
    left.descriptor.entry.historyRevision === right.descriptor.entry.historyRevision,
  );
}
