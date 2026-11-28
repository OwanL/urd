import { useCallback, useLayoutEffect, useRef, useState } from "preact/hooks";
import { openBitwardenPage } from "./BitwardenPage.ts";
import type { BrowserPage } from "./BrowserPage.ts";

type BitwardenState =
  | { kind: "opening" }
  | { kind: "open"; page: BrowserPage }
  | { kind: "failed"; message: string };

export function useBitwardenPage(sessionId: string, enabled: boolean) {
  const [state, setState] = useState<BitwardenState | null>(null);
  const lease = useRef<ReturnType<typeof openBitwardenPage> | null>(null);
  const revision = useRef(0);

  const close = useCallback(() => {
    const current = lease.current;
    lease.current = null;
    const closing = ++revision.current;
    setState(null);
    void current?.release().catch(() => {
      if (revision.current === closing)
        setState({ kind: "failed", message: "Bitwarden did not close." });
    });
  }, []);

  useLayoutEffect(() => {
    setState(null);
    return () => {
      const current = lease.current;
      lease.current = null;
      revision.current += 1;
      void current?.release().catch(() => {});
    };
  }, [sessionId, enabled]);

  const toggle = () => {
    if (!enabled) return;
    if (lease.current) {
      close();
      return;
    }
    revision.current += 1;
    const current = openBitwardenPage(sessionId);
    lease.current = current;
    setState({ kind: "opening" });
    void current.page.then(
      (page) => {
        if (lease.current === current && page) setState({ kind: "open", page });
      },
      () => {
        if (lease.current !== current) return;
        lease.current = null;
        setState({ kind: "failed", message: "Bitwarden did not open." });
      },
    );
  };

  return { state: enabled ? state : null, toggle, close };
}
