import { requestJson } from "../../http/Requests.ts";

export interface BrowserPage {
  id: string;
  ownerId: string;
  title: string;
  url: string;
  active: boolean;
}

export type BrowserPageCommand =
  | { type: "resize"; width: number; height: number }
  | { type: "click"; x: number; y: number }
  | { type: "wheel"; dx: number; dy: number; x: number; y: number }
  | { type: "key"; key: string }
  | { type: "text"; text: string };

// Tab labels fall back to the page's host until the agent sets a title.
export function pageTitle(page: BrowserPage): string {
  if (page.title) return page.title;
  try {
    return new URL(page.url).hostname || page.url || page.id;
  } catch {
    return page.url || page.id;
  }
}

interface BrowserPages {
  pages: readonly BrowserPage[];
  bitwarden: boolean;
}

export function readBrowserPages(sessionId: string, signal: AbortSignal): Promise<BrowserPages> {
  return requestJson(browserPagesUrl(sessionId), "GET", undefined, signal);
}

export function browserPagesUrl(sessionId: string, path = ""): URL {
  return new URL(`/api/sessions/${encodeURIComponent(sessionId)}/browser${path}`, location.origin);
}
