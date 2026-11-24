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

export async function readBrowserPages(
  sessionId: string,
  signal?: AbortSignal,
): Promise<readonly BrowserPage[]> {
  const result = await requestJson<{ pages: readonly BrowserPage[] }>(
    new URL(`/api/sessions/${encodeURIComponent(sessionId)}/browser`, location.origin),
    "GET",
    undefined,
    signal,
  );
  return result.pages ?? [];
}
