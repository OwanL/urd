import { requestEmpty, requestJson } from "../../http/Requests.ts";

export interface SnoozedChat {
  readonly sessionId: string;
  readonly until: string;
  readonly title: string | null;
}

export interface SnoozedChatsSnapshot {
  readonly now: string;
  readonly sessions: readonly SnoozedChat[];
}

export class SnoozeClient {
  private readonly origin: URL;

  constructor(origin: URL) {
    this.origin = origin;
  }

  read(signal: AbortSignal): Promise<SnoozedChatsSnapshot> {
    return requestJson(new URL("/api/sessions/snoozed", this.origin), "GET", undefined, signal);
  }

  change(id: string, change: { until: string } | { expectedUntil: string }): Promise<void> {
    return requestEmpty(
      new URL(`/api/sessions/${encodeURIComponent(id)}/snooze`, this.origin),
      "POST",
      change,
      AbortSignal.timeout(15_000),
    );
  }
}
