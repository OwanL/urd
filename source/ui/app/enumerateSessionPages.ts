import type { SessionClient } from "../sessions/SessionClient.ts";
import type { SessionIdPage } from "../sessions/SessionIdPage.ts";

export async function* enumerateSessionPages(
  client: Pick<SessionClient, "list">,
  signal: AbortSignal,
): AsyncGenerator<SessionIdPage> {
  let cursor: string | undefined;
  do {
    if (signal.aborted) return;
    const page = await client.list(cursor, signal);
    if (signal.aborted) return;
    yield page;
    cursor = page.nextCursor ?? undefined;
  } while (cursor !== undefined);
}
