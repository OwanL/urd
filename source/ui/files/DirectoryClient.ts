import { requestJson } from "../http/requestJson.ts";

export interface DirectoryEntry {
  readonly name: string;
  readonly path: string;
}

export interface DirectoryListing {
  readonly path: string | null;
  readonly parentPath: string | null;
  readonly directories: readonly DirectoryEntry[];
  readonly truncated: boolean;
}

// Lists local directories through the browser API; construction performs no I/O.
export class DirectoryClient {
  private readonly origin: string;

  constructor(origin: URL) {
    this.origin = origin.origin;
  }

  list(path: string | null, filter?: string, signal?: AbortSignal): Promise<DirectoryListing> {
    const url = new URL("/api/files/directories", this.origin);
    if (path !== null) url.searchParams.set("path", path);
    if (filter) url.searchParams.set("filter", filter);
    return requestJson(url, "GET", undefined, signal);
  }
}
