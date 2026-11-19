import { requestJson } from "../http/Requests.ts";
import type { GlobalUsageQuery, UsageQuery, UsageSummary } from "./Usage.ts";

export class UsageClient {
  private readonly origin: string;

  constructor(origin: URL) {
    this.origin = origin.origin;
  }

  getSummary(query: UsageQuery, signal?: AbortSignal): Promise<UsageSummary> {
    const url = new URL("/api/usage", this.origin);
    if ("sessionId" in query) {
      if (!/^[\da-f]{8}(-[\da-f]{4}){3}-[\da-f]{12}$/i.test(query.sessionId))
        throw new TypeError("Usage session identity must be a GUID.");
      url.searchParams.set("sessionId", query.sessionId);
    } else {
      validateGlobalRange(query);
      url.searchParams.set("from", query.from);
      url.searchParams.set("to", query.to);
    }
    if (query.detail === false) url.searchParams.set("detail", "false");
    return requestJson(url, "GET", undefined, signal);
  }
}

function validateGlobalRange(query: GlobalUsageQuery): void {
  const from = Date.parse(query.from);
  const to = Date.parse(query.to);
  if (!Number.isFinite(from) || !Number.isFinite(to) || to <= from)
    throw new TypeError("Usage range must have valid increasing ISO timestamps.");
  if (to - from > 31 * 24 * 60 * 60 * 1_000)
    throw new TypeError("Usage range cannot exceed 31 days.");
}
