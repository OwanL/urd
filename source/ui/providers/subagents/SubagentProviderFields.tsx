import type { ProviderConnection } from "../ProviderConnection.ts";
import { toggleSubagentProvider } from "./SubagentProviderSelection.ts";
import type { SubagentProviderConnectionIds } from "./SubagentProviderSelection.ts";
import "./subagent-providers.css";

export function SubagentProviderFields({
  label,
  connections,
  selection,
  disabled,
  onChange,
}: {
  label: string;
  connections: readonly ProviderConnection[];
  selection: SubagentProviderConnectionIds;
  disabled: boolean;
  onChange(connectionIds: SubagentProviderConnectionIds): void;
}) {
  const connectedIds = connections.map(({ id }) => id);
  const available = new Set(connectedIds);
  const explicit = selection === null ? null : new Set(selection);
  const unavailable = selection?.filter((id) => !available.has(id)) ?? [];
  const checkbox = (id: string, name: string, checked: boolean) => (
    <ProviderCheckbox
      key={id}
      name={name}
      checked={checked}
      disabled={disabled}
      onChange={(next) => onChange(toggleSubagentProvider(selection, connectedIds, id, next))}
    />
  );

  return (
    <fieldset class="subagent-provider-fields">
      <legend>{label}</legend>
      <div class="subagent-provider-mode">
        <ProviderCheckbox
          name="All connected providers"
          checked={selection === null}
          disabled={disabled}
          onChange={(next) => onChange(next ? null : [])}
        />
      </div>
      {connections.length === 0 && <p>No provider connections.</p>}
      {connections.map(({ id, name }) => checkbox(id, name, explicit?.has(id) ?? true))}
      {unavailable.map((id) => checkbox(id, `Unavailable connection · ${id}`, true))}
    </fieldset>
  );
}

function ProviderCheckbox({
  name,
  checked,
  disabled,
  onChange,
}: {
  name: string;
  checked: boolean;
  disabled: boolean;
  onChange(checked: boolean): void;
}) {
  return (
    <label class="subagent-provider-option">
      <input
        type="checkbox"
        checked={checked}
        aria-disabled={disabled || undefined}
        onChange={(event) => {
          // Keep the current checkbox focused when a save temporarily prevents edits.
          if (disabled) event.currentTarget.checked = checked;
          else onChange(event.currentTarget.checked);
        }}
      />
      <span>{name}</span>
    </label>
  );
}
