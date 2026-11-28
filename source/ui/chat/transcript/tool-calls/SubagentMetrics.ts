import type { SubagentCall, SubagentCostAmount } from "../../../sessions/SubagentCall.ts";
import type { TranscriptToolFacts } from "../../../sessions/transcript/Messages.ts";
import { formatCostUsd } from "../../../usage/UsagePresentation.ts";

export function shortSubagentModel(name: string): string {
  return name.replace(/^Claude[ -](?=(?:Opus|Sonnet|Haiku)\b)/i, "");
}

export function subagentCostLabel(amount: SubagentCostAmount): string {
  const value = formatCostUsd(amount.usd);
  return amount.usd !== null && amount.partial ? `~${value}` : value;
}

export function subagentElapsed(
  child: SubagentCall | null,
  tool: TranscriptToolFacts,
  now: number,
): { text: string; description: string; running: boolean } {
  const started = Date.parse(child?.startedAt ?? "");
  const running =
    child?.live === true && Number.isFinite(started) && child.elapsedMilliseconds === null;
  const milliseconds = running ? Math.max(0, now - started) : (child?.elapsedMilliseconds ?? null);
  const incomplete = child?.durationIncomplete === true;
  const status = tool.interrupted
    ? "Interrupted"
    : tool.outcome === "failed"
      ? "Failed"
      : tool.outcome === "cancelled"
        ? "Cancelled"
        : running
          ? "Running"
          : "";
  if (milliseconds === null) {
    const text = status || (!tool.started && !tool.isClosed ? "Queued" : "—");
    return { text, description: text === "—" ? "Elapsed time unavailable" : text, running };
  }

  const seconds = Math.floor(milliseconds / 1_000);
  const minutes = Math.floor(seconds / 60);
  const hours = Math.floor(minutes / 60);
  const clock = (value: number) => String(value).padStart(2, "0");
  const duration =
    hours > 0
      ? `${hours}:${clock(minutes % 60)}:${clock(seconds % 60)}`
      : `${minutes}:${clock(seconds % 60)}`;
  const scope = incomplete ? "Elapsed time incomplete" : "Elapsed time";
  return {
    text: incomplete ? `≥${duration}` : duration,
    description: `${status ? `${status} · ` : ""}${scope}: ${duration}`,
    running,
  };
}
