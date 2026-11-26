import { useRef } from "preact/hooks";
import type { McpAuthentication } from "./McpServer.ts";
import type { McpConnectProblem } from "./McpConnectRequest.ts";
import type { McpSettingsState } from "./useMcpSettings.ts";
import "./mcp-settings.css";

export function McpSettings({ state }: { state: McpSettingsState }) {
  const heading = useRef<HTMLHeadingElement>(null);
  const urlInput = useRef<HTMLInputElement>(null);
  const headerInput = useRef<HTMLInputElement>(null);
  const credentialInput = useRef<HTMLInputElement>(null);
  const server = state.servers?.find((item) => item.id === state.selectedId);
  const pending = state.busy || !!state.operationId;
  const focusProblemField = (problem: McpConnectProblem | null) => {
    if (problem === "url") urlInput.current?.focus();
    else if (problem === "headerName") headerInput.current?.focus();
    else if (problem === "credential") credentialInput.current?.focus();
  };
  const go = (screen: "list" | "add" | "detail") => {
    state.actions.show(screen);
    requestAnimationFrame(() => heading.current?.focus());
  };
  const submit = (event: Event) => {
    event.preventDefault();
    focusProblemField(state.actions.connect());
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
                      state.actions.select(item.id);
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
              value={state.form.url}
              disabled={pending}
              onInput={(e) => state.actions.editForm({ url: e.currentTarget.value })}
            />
          </label>
          <label>
            Authentication
            <select
              value={state.form.authentication}
              disabled={pending}
              onChange={(e) =>
                state.actions.editForm({
                  authentication: e.currentTarget.value as McpAuthentication,
                })
              }
            >
              <option value="automatic">Automatic</option>
              <option value="bearerToken">Bearer token</option>
              <option value="customHeader">Custom header</option>
            </select>
          </label>
          {state.form.authentication === "customHeader" && (
            <label>
              Header name
              <input
                ref={headerInput}
                value={state.form.headerName}
                disabled={pending}
                onInput={(e) => state.actions.editForm({ headerName: e.currentTarget.value })}
              />
            </label>
          )}
          {state.form.authentication !== "automatic" && (
            <label>
              {state.form.authentication === "bearerToken" ? "Bearer token" : "Header value"}
              <input
                ref={credentialInput}
                type="password"
                autocomplete="off"
                value={state.form.credential}
                disabled={pending}
                onInput={(e) => state.actions.editForm({ credential: e.currentTarget.value })}
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
              onClick={() => state.actions.reconnect(server.id)}
            >
              Reconnect
            </button>
            <button
              type="button"
              disabled={pending}
              onClick={() => void state.actions.remove(server.id)}
            >
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
          <button type="button" disabled={state.busy} onClick={() => void state.actions.cancel()}>
            {state.operation?.state === "failed" ? "Dismiss" : "Cancel"}
          </button>
        </div>
      )}
      {state.error && (
        <div class="mcp-error" role="alert">
          {state.error}
          {state.operationId && !state.busy && (
            <button type="button" onClick={state.actions.stopTracking}>
              Stop tracking
            </button>
          )}
        </div>
      )}
      {state.loadError && (
        <div class="mcp-error" role="alert">
          {state.loadError}
          <button type="button" onClick={() => void state.actions.read()}>
            Retry
          </button>
        </div>
      )}
    </div>
  );
}
