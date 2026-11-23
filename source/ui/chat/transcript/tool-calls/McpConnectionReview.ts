import type { TranscriptToolFacts } from "../../../sessions/transcript/Messages.ts";

export function canReviewMcpConnection(tool: TranscriptToolFacts, completion: boolean): boolean {
  return (
    !completion &&
    tool.name === "propose_mcp_connection" &&
    tool.started &&
    tool.isClosed &&
    !tool.interrupted &&
    tool.outcome === "completed"
  );
}

export interface McpConnectionReviewDetail {
  readonly url: string;
}

export function proposedMcpUrl(input: string): string | null {
  try {
    const args: unknown = JSON.parse(input);
    if (!args || typeof args !== "object" || Array.isArray(args) || !("url" in args)) return null;
    const value = args.url;
    if (
      typeof value !== "string" ||
      value.length > 2048 ||
      value !== value.trim() ||
      /[?#\\\u0000-\u001f\u007f]/.test(value)
    )
      return null;
    // URL normalisation can erase empty userinfo and delimiters, so inspect the authority too.
    const authority = /^[a-z][a-z\d+.-]*:\/\/([^/]+)/i.exec(value)?.[1];
    if (!authority || authority.includes("@")) return null;
    const url = new URL(value);
    if (url.username || url.password || url.search || url.hash) return null;
    if (url.protocol === "https:") return value;
    if (url.protocol !== "http:") return null;
    const host = url.hostname.toLowerCase();
    if (host === "localhost" || host === "[::1]" || /^127(?:\.\d{1,3}){3}$/.test(host))
      return value;
    return null;
  } catch {
    return null;
  }
}
