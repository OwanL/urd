import { useEffect, useId, useRef, useState } from "preact/hooks";
import { Icon } from "../components/Icon.tsx";
import { Popover } from "../components/Popover.tsx";
import { Tooltip } from "../components/Tooltip.tsx";
import type { ProviderClient } from "../providers/ProviderClient.ts";
import type { ProviderConnection } from "../providers/ProviderConnection.ts";
import { SubagentProviderFields } from "../providers/SubagentProviderFields.tsx";
import type {
  SubagentProviderConnectionIds,
  SubagentProviders,
} from "../providers/SubagentProviderSelection.ts";
import type { SessionClient } from "../sessions/SessionClient.ts";

interface SubagentProviderControlProps {
  sessionId: string;
  sessionClient: SessionClient;
  providerClient: ProviderClient;
  ready: boolean;
  saving: boolean;
  saveError: string | null;
  onSave(ids: SubagentProviderConnectionIds): Promise<SubagentProviders | null>;
}

interface SubagentProviderData extends SubagentProviders {
  readonly connections: readonly ProviderConnection[];
}

export function SubagentProviderControl(props: SubagentProviderControlProps) {
  const [open, setOpen] = useState(false);
  const anchor = useRef<HTMLButtonElement>(null);
  const id = useId();
  const label = props.saveError ? "Subagent providers: save unconfirmed" : "Subagent providers";
  return (
    <div class="composer-subagent-providers">
      <Tooltip content={props.saving ? "Saving subagent providers…" : label} disabled={open}>
        <button
          ref={anchor}
          type="button"
          class="icon-button composer-subagent-trigger"
          aria-label={label}
          aria-busy={props.saving}
          data-save-error={props.saveError ? "true" : undefined}
          aria-haspopup="dialog"
          aria-expanded={open}
          aria-controls={open ? id : undefined}
          onClick={() => setOpen((value) => !value)}
        >
          <Icon name="subagents" />
        </button>
      </Tooltip>
      {open && (
        <Popover
          id={id}
          anchor={anchor}
          label="Session subagent providers"
          class="composer-subagent-popover"
          placement="above"
          onClose={() => setOpen(false)}
        >
          {props.ready ? (
            <SubagentProviderMenu {...props} />
          ) : (
            <p role="status">Subagent providers unavailable while disconnected.</p>
          )}
          {props.saveError && (
            <p class="composer-subagent-error" role="alert">
              {props.saveError}
            </p>
          )}
        </Popover>
      )}
      {!open && props.saveError && (
        <span class="composer-subagent-announcement" role="alert">
          {props.saveError}
        </span>
      )}
    </div>
  );
}

function SubagentProviderMenu({
  sessionId,
  sessionClient,
  providerClient,
  saving,
  onSave,
}: SubagentProviderControlProps) {
  const [data, setData] = useState<SubagentProviderData | null>(null);
  const [failure, setFailure] = useState(false);
  const [attempt, setAttempt] = useState(0);
  const dispatching = useRef(false);
  // Reopening while a save is outstanding waits for that write before reading its result.
  const needsRead = data === null && !saving;
  useEffect(() => {
    if (!needsRead) return;
    const abort = new AbortController();
    void Promise.all([
      sessionClient.getSubagentProviders(sessionId, abort.signal),
      providerClient.list(abort.signal),
    ]).then(
      ([policy, connections]) => {
        if (!abort.signal.aborted) setData({ ...policy, connections });
      },
      () => {
        if (!abort.signal.aborted) setFailure(true);
      },
    );
    return () => abort.abort();
  }, [attempt, needsRead, providerClient, sessionClient, sessionId]);

  async function choose(connectionIds: SubagentProviderConnectionIds) {
    if (!data || saving || dispatching.current) return;
    dispatching.current = true;
    setData({ ...data, connectionIds });
    try {
      const result = await onSave(connectionIds);
      // After a lost acknowledgement, read back the policy instead of assuming rollback.
      setData(result ? { ...result, connections: data.connections } : null);
    } finally {
      dispatching.current = false;
    }
  }

  return (
    <>
      {data && (
        <SubagentProviderFields
          label="Subagent providers"
          connections={data.connections}
          selection={data.connectionIds}
          disabled={saving}
          onChange={(ids) => void choose(ids)}
        />
      )}
      {saving ? (
        <p role="status">Saving…</p>
      ) : failure ? (
        <div class="general-settings-feedback">
          <p role="alert">Subagent providers could not be loaded.</p>
          <button
            type="button"
            onClick={() => {
              setFailure(false);
              setAttempt((value) => value + 1);
            }}
          >
            Retry
          </button>
        </div>
      ) : !data ? (
        <p role="status">Loading…</p>
      ) : null}
    </>
  );
}
