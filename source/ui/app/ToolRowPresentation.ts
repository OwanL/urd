import type { TranscriptToolFacts, ToolCallLabel } from "../sessions/Messages.ts";

export function toolCallLabelKey(tool: Pick<TranscriptToolFacts, "requestId" | "callId">): string {
  return `${tool.requestId}/${tool.callId}`;
}

export function toolCallLabel(
  tool: Pick<TranscriptToolFacts, "requestId" | "callId">,
  labels: Readonly<Record<string, ToolCallLabel>>,
): ToolCallLabel | null {
  return labels[toolCallLabelKey(tool)] ?? null;
}

export function isToolCallRunning(tool: TranscriptToolFacts, completion: boolean): boolean {
  return (
    !completion &&
    tool.started &&
    tool.outcome === null &&
    !Number.isFinite(Date.parse(tool.endedAt ?? ""))
  );
}

export function toolElapsedMilliseconds(
  tool: TranscriptToolFacts,
  now: number,
  completion: boolean,
): number | null {
  const startedAt = Date.parse(tool.startedAt ?? "");
  if (!Number.isFinite(startedAt)) return null;
  if (isToolCallRunning(tool, completion)) return Math.max(0, now - startedAt);
  const endedAt = Date.parse(tool.endedAt ?? "");
  return Number.isFinite(endedAt) ? Math.max(0, endedAt - startedAt) : null;
}

export function toolRowTimeLabel(
  tool: Pick<TranscriptToolFacts, "background">,
  elapsed: string | null,
  completion: boolean,
): string | null {
  if (!tool.background || completion) return null;
  return elapsed === null ? "Background call" : `Background call, elapsed ${elapsed}`;
}

export function formatElapsedTime(milliseconds: number): string | null {
  if (!Number.isFinite(milliseconds) || milliseconds < 5_000) return null;
  const seconds = Math.floor(milliseconds / 1_000);
  const minutes = Math.floor(seconds / 60);
  const remainingSeconds = seconds % 60;
  return minutes === 0 ? `${seconds}s` : `${minutes}m ${remainingSeconds}s`;
}
