import { useEffect, useId, useRef, useState } from "preact/hooks";
import { RequestError } from "../http/RequestError.ts";
import { requestJson } from "../http/Requests.ts";
import "./lan-hosting-setting.css";

interface BitwardenStatus {
  enabled: boolean;
  active: boolean;
  installed: boolean;
}

const endpoint = () => new URL("/api/browser/bitwarden", location.origin);

function message(error: unknown): string {
  if (
    error instanceof RequestError &&
    typeof error.responseBody === "object" &&
    error.responseBody !== null &&
    "error" in error.responseBody &&
    typeof error.responseBody.error === "string"
  )
    return error.responseBody.error;
  return "Could not update or read Bitwarden. Check the connection and try again.";
}

export function BitwardenSetting() {
  const descriptionId = useId();
  const [status, setStatus] = useState<BitwardenStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [pending, setPending] = useState(false);
  const mounted = useRef(false);
  const sequence = useRef(0);
  const busy = useRef(false);

  const refresh = async (preserveError = false) => {
    if (busy.current) return;
    const current = ++sequence.current;
    try {
      const result = await requestJson<BitwardenStatus>(endpoint(), "GET");
      if (!mounted.current || current !== sequence.current) return;
      setStatus(result);
      if (!preserveError) setError(null);
    } catch (failure) {
      if (!mounted.current || current !== sequence.current) return;
      setStatus(null);
      if (!preserveError) setError(message(failure));
    }
  };

  useEffect(() => {
    mounted.current = true;
    const requests = sequence;
    void refresh();
    return () => {
      mounted.current = false;
      requests.current++;
    };
  }, []);

  const change = async (enabled: boolean) => {
    if (busy.current) return;
    busy.current = true;
    const current = ++sequence.current;
    setPending(true);
    setError(null);
    let failed = false;
    try {
      const result = await requestJson<BitwardenStatus>(endpoint(), "PUT", { enabled });
      if (mounted.current && current === sequence.current) setStatus(result);
    } catch (failure) {
      failed = true;
      if (mounted.current && current === sequence.current) {
        setStatus(null);
        setError(message(failure));
      }
    } finally {
      busy.current = false;
      if (mounted.current && current === sequence.current) {
        setPending(false);
        if (failed) void refresh(true);
      }
    }
  };

  const needsInstall = status?.enabled && !status.active && !status.installed;

  return (
    <section class="lan-hosting-setting" aria-label="Bitwarden" aria-busy={pending}>
      <div class="lan-hosting-heading">
        <span class="lan-hosting-title">Bitwarden</span>
        <button
          class="lan-hosting-switch"
          type="button"
          role="switch"
          aria-label="Bitwarden"
          aria-describedby={descriptionId}
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
      <p id={descriptionId}>
        The vault is shared with the Urd browser and is not a security boundary.
      </p>
      {pending && <p role="status">Updating…</p>}
      {status && !needsInstall && status.enabled !== status.active && (
        <p role="status">Restart Urd to apply.</p>
      )}
      {needsInstall && !pending && (
        <>
          {!error && <p role="status">Bitwarden isn’t installed.</p>}
          <button type="button" onClick={() => void change(true)}>
            Retry install
          </button>
        </>
      )}
      {error && (
        <p class="lan-hosting-error" role="alert">
          {error}
        </p>
      )}
      {!status && error && !pending && (
        <button type="button" onClick={() => void refresh()}>
          Retry
        </button>
      )}
    </section>
  );
}
