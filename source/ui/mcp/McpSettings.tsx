import { useEffect, useRef, useState } from "preact/hooks";
import { requestJson } from "../http/Requests.ts";
import { RequestError } from "../http/RequestError.ts";
import "./mcp-settings.css";

type Authentication = "automatic" | "bearerToken" | "customHeader";
interface McpServer {
  id: string;
  name: string;
  url: string;
  authentication: Authentication;
  tools: string[];
}
interface Operation {
  state: "connecting" | "signIn" | "connected" | "failed";
  authorizationUrl: string | null;
  message: string | null;
}
const endpoint = (path: string) => new URL(`/api/${path}`, window.location.origin);
const errorMessage = (error: unknown) =>
  error instanceof Error ? error.message : "Request failed.";

// Owned by Settings so drafts and accepted operations survive closing the popover.
export function useMcpSettings(active: boolean, draft?: { url: string }) {
  const [servers, setServers] = useState<McpServer[] | null>(null);
  const [screen, setScreen] = useState<"list" | "add" | "detail">(draft ? "add" : "list");
  const [selected, setSelected] = useState<string | null>(null);
  const [url, setUrl] = useState(draft?.url ?? "");
  const [authentication, setAuthentication] = useState<Authentication>("automatic");
  const [headerName, setHeaderName] = useState("");
  const [credential, setCredential] = useState("");
  const [error, setError] = useState("");
  const [loadError, setLoadError] = useState("");
  const [incomingDraft, setIncomingDraft] = useState<{ url: string } | null>(null);
  const [busy, setBusy] = useState(false);
  const [operationId, setOperationId] = useState<string | null>(null);
  const [operation, setOperation] = useState<Operation | null>(null);
  const command = useRef(false);
  const previousDraft = useRef(draft);
  const generation = useRef(0);
  const pollGeneration = useRef(0);

  function clearForm() {
    setUrl("");
    setAuthentication("automatic");
    setHeaderName("");
    setCredential("");
  }
  function acceptDraft(next: { url: string }) {
    clearForm();
    setUrl(next.url);
    setScreen("add");
    setError("");
    setIncomingDraft(null);
  }
  useEffect(() => {
    if (draft && draft !== previousDraft.current) {
      if (
        command.current ||
        operationId ||
        (screen === "add" && (url || authentication !== "automatic" || headerName || credential))
      )
        setIncomingDraft(draft);
      else acceptDraft(draft);
    }
    previousDraft.current = draft;
  }, [draft, operationId, screen, url, authentication, headerName, credential]);
  async function reload() {
    const version = ++generation.current;
    try {
      const result = await requestJson<McpServer[]>(endpoint("mcp-servers"), "GET");
      if (version === generation.current) {
        setServers(result);
        setLoadError("");
      }
    } catch (e) {
      if (version === generation.current)
        setLoadError(`Could not load servers: ${errorMessage(e)}`);
    }
  }
  useEffect(() => {
    if (active) void reload();
  }, [active]);
  useEffect(() => {
    if (!operationId) return;
    let stopped = false;
    const version = ++pollGeneration.current;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const poll = async () => {
      try {
        const result = await requestJson<Operation>(
          endpoint(`mcp-operations/${encodeURIComponent(operationId)}`),
          "GET",
        );
        if (stopped || version !== pollGeneration.current) return;
        setOperation(result);
        setError("");
        if (result.state === "connected") {
          setOperationId(null);
          setOperation(null);
          clearForm();
          setScreen("list");
          void reload();
        } else if (result.state !== "failed") timer = setTimeout(() => void poll(), 1000);
      } catch (e) {
        if (!stopped && version === pollGeneration.current) {
          if (e instanceof RequestError && e.status === 404) {
            setError("");
            setOperation({
              state: "failed",
              authorizationUrl: null,
              message: "Connection operation no longer available. Check the server list.",
            });
            setCredential("");
            void reload();
          } else {
            setError(`Could not check connection: ${errorMessage(e)}`);
            timer = setTimeout(() => void poll(), 3000);
          }
        }
      }
    };
    void poll();
    return () => {
      stopped = true;
      if (timer) clearTimeout(timer);
    };
  }, [operationId]);

  async function start(path: string, body: unknown) {
    if (command.current || operationId) return;
    command.current = true;
    setBusy(true);
    setError("");
    setOperation(null);
    try {
      const result = await requestJson<{ id: string }>(endpoint(path), "POST", body);
      setOperationId(result.id);
    } catch (e) {
      setError(`Could not start connection: ${errorMessage(e)}`);
    } finally {
      command.current = false;
      setBusy(false);
    }
  }
  async function cancel() {
    if (!operationId || command.current) return;
    if (operation?.state === "failed") {
      setOperationId(null);
      setOperation(null);
      setCredential("");
      setError("");
      return;
    }
    command.current = true;
    setBusy(true);
    setError("");
    try {
      await requestJson<unknown>(
        endpoint(`mcp-operations/${encodeURIComponent(operationId)}`),
        "DELETE",
      );
      ++pollGeneration.current;
      setOperationId(null);
      setOperation(null);
      setCredential("");
      void reload();
    } catch (e) {
      if (e instanceof RequestError && e.status === 404) {
        ++pollGeneration.current;
        setOperationId(null);
        setOperation(null);
        setCredential("");
        void reload();
      } else setError(`Could not cancel connection: ${errorMessage(e)}`);
    } finally {
      command.current = false;
      setBusy(false);
    }
  }
  async function remove(id: string) {
    if (command.current || operationId) return;
    command.current = true;
    setBusy(true);
    setError("");
    try {
      await requestJson<unknown>(endpoint(`mcp-servers/${encodeURIComponent(id)}`), "DELETE");
      setScreen("list");
      await reload();
    } catch (e) {
      setError(`Could not remove server: ${errorMessage(e)}`);
    } finally {
      command.current = false;
      setBusy(false);
    }
  }
  return {
    servers,
    screen,
    setScreen,
    selected,
    setSelected,
    url,
    setUrl,
    authentication,
    setAuthentication,
    headerName,
    setHeaderName,
    credential,
    setCredential,
    error,
    setError,
    loadError,
    incomingDraft,
    acceptDraft,
    dismissDraft: () => setIncomingDraft(null),
    clearForm,
    stopTracking: () => {
      ++pollGeneration.current;
      setOperationId(null);
      setOperation(null);
      setCredential("");
      setError("Connection status unknown. Check the server list.");
      void reload();
    },
    busy,
    operationId,
    operation,
    start,
    cancel,
    remove,
    reload,
  };
}

type State = ReturnType<typeof useMcpSettings>;
export function McpSettings({ state }: { state: State }) {
  const heading = useRef<HTMLHeadingElement>(null);
  const urlInput = useRef<HTMLInputElement>(null);
  const headerInput = useRef<HTMLInputElement>(null);
  const credentialInput = useRef<HTMLInputElement>(null);
  const server = state.servers?.find((item) => item.id === state.selected);
  const pending = state.busy || !!state.operationId;
  const go = (screen: "list" | "add" | "detail") => {
    state.setError("");
    if (!pending && (screen === "add" || (screen === "list" && state.screen === "add")))
      state.clearForm();
    state.setScreen(screen);
    requestAnimationFrame(() => heading.current?.focus());
  };
  const submit = (event: Event) => {
    event.preventDefault();
    try {
      const parsed = new URL(state.url.trim());
      if (parsed.protocol !== "http:" && parsed.protocol !== "https:") throw new Error();
    } catch {
      state.setError("Enter a valid HTTP or HTTPS URL.");
      urlInput.current?.focus();
      return;
    }
    if (state.authentication === "customHeader" && !state.headerName.trim()) {
      state.setError("Enter a header name.");
      headerInput.current?.focus();
      return;
    }
    if (state.authentication !== "automatic" && !state.credential.trim()) {
      state.setError("Enter a credential.");
      credentialInput.current?.focus();
      return;
    }
    void state.start("mcp-servers", {
      url: state.url.trim(),
      authentication: state.authentication,
      headerName: state.authentication === "customHeader" ? state.headerName.trim() : null,
      credential: state.authentication === "automatic" ? null : state.credential,
    });
  };
  let authorizationUrl: string | null = null;
  try {
    const parsed = new URL(state.operation?.authorizationUrl ?? "");
    if (parsed.protocol === "http:" || parsed.protocol === "https:") authorizationUrl = parsed.href;
  } catch {
    /* No usable sign-in link. */
  }
  return (
    <div class="mcp-settings">
      {state.incomingDraft && (
        <div role="status">
          <p>A chat MCP draft is waiting.</p>
          <button
            type="button"
            disabled={pending}
            onClick={() => state.acceptDraft(state.incomingDraft!)}
          >
            Replace with chat draft
          </button>
          <button type="button" onClick={state.dismissDraft}>
            Dismiss
          </button>
        </div>
      )}
      {state.screen !== "list" && (
        <button type="button" class="mcp-back" onClick={() => go("list")}>
          ‹ Back to servers
        </button>
      )}
      <header class="mcp-heading">
        <h3 ref={heading} tabIndex={-1}>
          {state.screen === "list"
            ? "Servers"
            : state.screen === "add"
              ? "Add MCP"
              : (server?.name ?? "Server unavailable")}
        </h3>
        {state.screen === "list" && (
          <button type="button" onClick={() => go("add")}>
            + Add MCP
          </button>
        )}
      </header>
      {state.screen === "list" && (
        <>
          {state.servers === null ? (
            <p>{state.loadError ? "Servers unavailable." : "Loading servers…"}</p>
          ) : state.servers.length === 0 ? (
            <p>No servers added.</p>
          ) : (
            <ul class="mcp-servers">
              {state.servers.map((item) => (
                <li key={item.id}>
                  <button
                    type="button"
                    onClick={() => {
                      state.setSelected(item.id);
                      go("detail");
                    }}
                  >
                    <strong>{item.name}</strong>
                    <span>Previously verified</span>
                    <span aria-hidden="true">›</span>
                  </button>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
      {state.screen === "add" && (
        <form onSubmit={submit}>
          <label>
            URL
            <input
              ref={urlInput}
              type="url"
              required
              value={state.url}
              disabled={pending}
              onInput={(e) => state.setUrl(e.currentTarget.value)}
            />
          </label>
          <label>
            Authentication
            <select
              value={state.authentication}
              disabled={pending}
              onChange={(e) => state.setAuthentication(e.currentTarget.value as Authentication)}
            >
              <option value="automatic">Automatic</option>
              <option value="bearerToken">Bearer token</option>
              <option value="customHeader">Custom header</option>
            </select>
          </label>
          {state.authentication === "customHeader" && (
            <label>
              Header name
              <input
                ref={headerInput}
                value={state.headerName}
                disabled={pending}
                onInput={(e) => state.setHeaderName(e.currentTarget.value)}
              />
            </label>
          )}
          {state.authentication !== "automatic" && (
            <label>
              {state.authentication === "bearerToken" ? "Bearer token" : "Header value"}
              <input
                ref={credentialInput}
                type="password"
                autocomplete="off"
                value={state.credential}
                disabled={pending}
                onInput={(e) => state.setCredential(e.currentTarget.value)}
              />
            </label>
          )}
          <button type="submit" disabled={pending}>
            Connect
          </button>
        </form>
      )}
      {state.screen === "detail" && server && (
        <div class="mcp-detail">
          <p>{server.url}</p>
          <p>Previously verified · Current status unknown</p>
          <p>
            Authentication:{" "}
            {server.authentication === "automatic"
              ? "Automatic"
              : server.authentication === "bearerToken"
                ? "Bearer token"
                : "Custom header"}
          </p>
          <div>
            <strong>Discovered tools ({server.tools.length})</strong>
            <p>
              Agents and subagents can call these tools in new turns, including in existing chats.
            </p>
            <p>
              Paused while LAN hosting is active. Changes apply to new turns; stopping a turn or
              removing this server may not undo effects of calls already in progress.
            </p>
            {server.tools.length > 0 && (
              <ul>
                {server.tools.map((tool) => (
                  <li key={tool}>{tool}</li>
                ))}
              </ul>
            )}
          </div>
          <div class="mcp-actions">
            <button
              type="button"
              disabled={pending}
              onClick={() =>
                void state.start(`mcp-servers/${encodeURIComponent(server.id)}/reconnect`, {})
              }
            >
              Reconnect
            </button>
            <button type="button" disabled={pending} onClick={() => void state.remove(server.id)}>
              Remove
            </button>
          </div>
        </div>
      )}
      {state.operationId && (
        <div class="mcp-operation" role="status">
          <p>
            {state.operation?.state === "signIn"
              ? "Sign in to continue."
              : state.operation?.state === "failed"
                ? "Connection failed."
                : "Connecting…"}
          </p>
          {state.operation?.message && <p>{state.operation.message}</p>}
          {state.operation?.state === "signIn" &&
            (authorizationUrl ? (
              <a href={authorizationUrl} target="_blank" rel="noopener noreferrer">
                Open sign-in
              </a>
            ) : (
              <p>Sign-in link unavailable.</p>
            ))}
          <button type="button" disabled={state.busy} onClick={() => void state.cancel()}>
            {state.operation?.state === "failed" ? "Dismiss" : "Cancel"}
          </button>
        </div>
      )}
      {state.error && (
        <div class="mcp-error" role="alert">
          {state.error}
          {state.operationId && !state.busy && (
            <button type="button" onClick={state.stopTracking}>
              Stop tracking
            </button>
          )}
        </div>
      )}
      {state.loadError && (
        <div class="mcp-error" role="alert">
          {state.loadError}
          <button type="button" onClick={() => void state.reload()}>
            Retry
          </button>
        </div>
      )}
    </div>
  );
}
