import { useEffect, useId, useRef, useState } from "preact/hooks";
import { RequestError } from "../http/RequestError.ts";
import { requestJson } from "../http/Requests.ts";
import {
  acknowledged,
  lostAcknowledgement,
  observeRead,
  readApplies,
  type HostingDisplay,
  type HostingStatus,
} from "./LanHostingReconciliation.ts";
import "./lan-hosting-setting.css";

function message(error: unknown): string {
  if (
    error instanceof RequestError &&
    typeof error.responseBody === "object" &&
    error.responseBody !== null &&
    "error" in error.responseBody &&
    typeof error.responseBody.error === "string"
  )
    return error.responseBody.error;
  return "Could not update or read LAN hosting. Check the connection and try again.";
}

export function LanHostingSetting() {
  const disclaimerId = useId();
  const [display, setDisplay] = useState<HostingDisplay>({ status: null, error: null });
  const { status, error } = display;
  const [pending, setPending] = useState(false);
  const [copied, setCopied] = useState<string | null>(null);
  const sequence = useRef(0);
  const busy = useRef(false);
  const commandFailed = useRef(false);
  const refreshStatus = useRef<() => void>(() => {});

  useEffect(() => {
    let mounted = true;
    const requests = sequence;
    let reading = false;
    let queued = false;
    const refresh = () => {
      queued = true;
      if (!mounted || reading || busy.current) return;
      reading = true;
      void (async () => {
        while (mounted && queued && !busy.current) {
          queued = false;
          const current = ++sequence.current;
          try {
            const result = await requestJson<HostingStatus>(
              new URL("/api/hosting/lan", location.origin),
              "GET",
            );
            if (mounted && readApplies(current, sequence.current, busy.current))
              setDisplay((previous) =>
                observeRead(
                  { status: result, error: null },
                  commandFailed.current ? previous.error : null,
                ),
              );
          } catch (failure) {
            if (mounted && readApplies(current, sequence.current, busy.current))
              setDisplay((previous) =>
                observeRead(
                  { status: null, error: message(failure) },
                  commandFailed.current ? previous.error : null,
                ),
              );
          }
        }
        reading = false;
        if (mounted && queued && !busy.current) refresh();
      })();
    };
    refreshStatus.current = refresh;
    const onFocus = () => refresh();
    const onVisibilityChange = () => {
      if (document.visibilityState === "visible") refresh();
    };
    const interval = window.setInterval(() => {
      if (document.visibilityState === "visible") refresh();
    }, 5_000);
    void refresh();
    window.addEventListener("focus", onFocus);
    document.addEventListener("visibilitychange", onVisibilityChange);
    return () => {
      mounted = false;
      requests.current++;
      refreshStatus.current = () => {};
      window.clearInterval(interval);
      window.removeEventListener("focus", onFocus);
      document.removeEventListener("visibilitychange", onVisibilityChange);
    };
  }, []);

  const change = async (enabled: boolean) => {
    if (busy.current) return;
    busy.current = true;
    commandFailed.current = false;
    const current = ++sequence.current;
    setPending(true);
    setCopied(null);
    // A new command supersedes a remembered lost acknowledgement.
    setDisplay((previous) => ({ ...previous, error: null }));
    try {
      const result = await requestJson<HostingStatus>(
        new URL("/api/hosting/lan", location.origin),
        "POST",
        { enabled },
      );
      if (acknowledged(current, sequence.current))
        setDisplay((previous) => ({ ...previous, status: result }));
    } catch (failure) {
      if (acknowledged(current, sequence.current)) {
        commandFailed.current = true;
        // A failed response does not establish whether the command took effect.
        setDisplay(lostAcknowledgement(message(failure)));
      }
    } finally {
      busy.current = false;
      if (acknowledged(current, sequence.current)) setPending(false);
      // Reconcile the acknowledgement with tray changes made while the request was in flight.
      refreshStatus.current();
    }
  };

  const copy = async (origin: string) => {
    try {
      await navigator.clipboard.writeText(origin);
      setCopied(origin);
    } catch {
      setDisplay((previous) => ({ ...previous, error: "Could not copy the address." }));
    }
  };

  return (
    <section class="lan-hosting-setting" aria-label="LAN hosting" aria-busy={pending}>
      <div class="lan-hosting-heading">
        <span class="lan-hosting-title">LAN hosting</span>
        <button
          class="lan-hosting-switch"
          type="button"
          role="switch"
          aria-label="LAN hosting"
          aria-describedby={disclaimerId}
          aria-checked={status?.enabled ?? false}
          disabled={!status || pending}
          onClick={() => void change(!status?.enabled)}
        >
          <span class="lan-hosting-switch-track" aria-hidden="true">
            <span class="lan-hosting-switch-thumb" />
          </span>
          <span>{status ? (status.enabled ? "On" : "Off") : error ? "Unknown" : "Checking…"}</span>
        </button>
      </div>
      <p id={disclaimerId}>
        Only enable on a trusted network. Devices on this network can use Urd without signing in.
        This setting stays on after restart.
      </p>
      {pending && <p role="status">Updating…</p>}
      {error && (
        <p class="lan-hosting-error" role="alert">
          {error}
        </p>
      )}
      {status?.startupError && (
        <p class="lan-hosting-error" role="alert">
          {status.startupError}
        </p>
      )}
      {status?.remembered && !status.enabled && (
        <button type="button" disabled={pending} onClick={() => void change(false)}>
          Cancel saved restart
        </button>
      )}
      {status?.enabled &&
        status.origins.map((origin) => (
          <div class="lan-hosting-address" key={origin}>
            <code>{origin}</code>
            <button type="button" onClick={() => void copy(origin)}>
              Copy
            </button>
            {copied === origin && <span role="status">Copied</span>}
          </div>
        ))}
    </section>
  );
}
