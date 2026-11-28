import { RequestError } from "../../http/RequestError.ts";
import { requestEmpty, requestJson } from "../../http/Requests.ts";
import { browserPagesUrl, type BrowserPage } from "./BrowserPage.ts";

interface BitwardenPageLease {
  page: Promise<BrowserPage | null>;
  release(): Promise<void>;
}

const sessions = new Map<string, BitwardenSession>();

export function openBitwardenPage(sessionId: string): BitwardenPageLease {
  let session = sessions.get(sessionId);
  if (!session) {
    session = new BitwardenSession(sessionId);
    sessions.set(sessionId, session);
  }
  return session.open();
}

// The host reuses its vault tab per chat. A late POST and its cleanup must drain
// before a subsequent POST can reacquire that tab, even across viewer mounts.
class BitwardenSession {
  private tail = Promise.resolve();
  private page: BrowserPage | null = null;
  private readonly holders = new Set<symbol>();
  private readonly sessionId: string;

  constructor(sessionId: string) {
    this.sessionId = sessionId;
  }

  open(): BitwardenPageLease {
    const holder = Symbol();
    let released = false;
    const page = this.enqueue(async () => {
      if (released) return null;
      this.page ??= await requestJson<BrowserPage>(
        browserPagesUrl(this.sessionId, "/bitwarden"),
        "POST",
        {},
      );
      this.holders.add(holder);
      return this.page;
    });
    return {
      page,
      release: () => {
        if (released) return Promise.resolve();
        released = true;
        return this.enqueue(async () => {
          if (!this.holders.delete(holder) || this.holders.size || !this.page) return;
          const pageId = this.page.id;
          this.page = null;
          await closeBitwardenPage(this.sessionId, pageId);
        });
      },
    };
  }

  private enqueue<T>(operation: () => Promise<T>): Promise<T> {
    const pending = this.tail.then(operation);
    const drained = () => {
      if (this.tail === tail && !this.holders.size && sessions.get(this.sessionId) === this)
        sessions.delete(this.sessionId);
    };
    const tail = pending.then(drained, drained);
    this.tail = tail;
    return pending;
  }
}

async function closeBitwardenPage(sessionId: string, pageId: string): Promise<void> {
  try {
    await requestEmpty(
      browserPagesUrl(sessionId, `/bitwarden/${encodeURIComponent(pageId)}`),
      "DELETE",
      undefined,
    );
  } catch (error) {
    if (!(error instanceof RequestError && error.status === 404)) throw error;
  }
}
