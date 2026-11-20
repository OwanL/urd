import type { TranscriptToolFacts, ToolCallLabel } from "../../../sessions/transcript/Messages.ts";

export function toolCallLabelKey(tool: Pick<TranscriptToolFacts, "requestId" | "callId">): string {
  return `${tool.requestId}/${tool.callId}`;
}

export function toolCallLabel(
  tool: Pick<TranscriptToolFacts, "requestId" | "callId">,
  labels: Readonly<Record<string, ToolCallLabel>>,
): ToolCallLabel | null {
  return labels[toolCallLabelKey(tool)] ?? null;
}

export function isToolCallFailed(
  tool: Pick<TranscriptToolFacts, "outcome" | "started" | "interrupted">,
): boolean {
  return tool.outcome === "failed" && tool.started && !tool.interrupted;
}

export type ToolRowStatus =
  | "completed"
  | "failed"
  | "cancelled"
  | "interrupted"
  | "unknown"
  | "not-run"
  | "pending"
  | "running";

export function toolRowStatus(tool: TranscriptToolFacts, completion: boolean): ToolRowStatus {
  if (tool.interrupted) return tool.started ? "interrupted" : "not-run";
  if (tool.outcome !== null) return tool.outcome;
  if (tool.isClosed && !tool.started && !completion) return "not-run";
  if (tool.isClosed || Number.isFinite(Date.parse(tool.endedAt ?? "")) || completion)
    return "unknown";
  if (!tool.started) return "pending";
  return "running";
}

export function toolElapsedMilliseconds(
  tool: TranscriptToolFacts,
  now: number,
  completion: boolean,
): number | null {
  const startedAt = Date.parse(tool.startedAt ?? "");
  if (!Number.isFinite(startedAt)) return null;
  if (toolRowStatus(tool, completion) === "running") return Math.max(0, now - startedAt);
  const endedAt = Date.parse(tool.endedAt ?? "");
  return Number.isFinite(endedAt) ? Math.max(0, endedAt - startedAt) : null;
}

export function formatElapsedTime(milliseconds: number): string | null {
  if (!Number.isFinite(milliseconds) || milliseconds < 5_000) return null;
  const seconds = Math.floor(milliseconds / 1_000);
  const minutes = Math.floor(seconds / 60);
  const hours = Math.floor(minutes / 60);
  const remainingSeconds = seconds % 60;
  const remainingMinutes = minutes % 60;
  const remainingHours = hours % 24;
  if (hours >= 24) {
    const days = Math.floor(hours / 24);
    return remainingHours === 0 ? `${days}d` : `${days}d ${remainingHours}h`;
  }
  if (hours > 0) return remainingMinutes === 0 ? `${hours}h` : `${hours}h ${remainingMinutes}m`;
  return minutes === 0 ? `${seconds}s` : `${minutes}m ${remainingSeconds}s`;
}
