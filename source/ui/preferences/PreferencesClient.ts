import { requestJson } from "../http/requestJson.ts";

export interface Preferences {
  readonly workingDirectory: string | null;
  readonly compactionTriggerTokenCount: number | null;
}

// Typed access to browser preferences; construction performs no I/O.
export class PreferencesClient {
  private readonly origin: string;

  constructor(origin: URL) {
    this.origin = origin.origin;
  }

  get(signal?: AbortSignal): Promise<Preferences> {
    return requestJson(new URL("/api/preferences", this.origin), "GET", undefined, signal);
  }

  setWorkingDirectory(workingDirectory: string | null): Promise<Preferences> {
    return requestJson(new URL("/api/preferences/working-directory", this.origin), "POST", {
      workingDirectory,
    });
  }

  setCompactionTriggerTokenCount(compactionTriggerTokenCount: number | null): Promise<Preferences> {
    return requestJson(new URL("/api/preferences/compaction-trigger", this.origin), "POST", {
      compactionTriggerTokenCount,
    });
  }
}
