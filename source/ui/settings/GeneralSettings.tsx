import type { RefObject } from "preact";
import { useState } from "preact/hooks";
import { FolderPicker } from "../components/FolderPicker.tsx";
import type { WorkingDirectoryPreferenceState } from "../preferences/useWorkingDirectoryPreferences.ts";

interface GeneralSettingsProps {
  state: WorkingDirectoryPreferenceState;
  buttonRef: RefObject<HTMLButtonElement | null>;
}

export function GeneralSettings({ state, buttonRef }: GeneralSettingsProps) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const workingDirectory = state.preferences?.workingDirectory ?? null;

  return (
    <section
      class="general-settings"
      aria-label="General"
      aria-busy={state.loading || state.pending}
    >
      <div class="general-settings-field">
        <span>Working directory</span>
        <code class="general-settings-path">{workingDirectory ?? "Not set"}</code>
        <p class="general-settings-description">Used for new chats.</p>
      </div>
      <div class="general-settings-actions">
        <button
          ref={buttonRef}
          type="button"
          disabled={state.pending}
          onClick={() => setPickerOpen(true)}
        >
          Choose folder…
        </button>
        <button
          type="button"
          disabled={state.pending || !state.loaded || workingDirectory === null}
          onClick={() => void state.save(null)}
        >
          Clear
        </button>
      </div>
      <div class="general-settings-feedback" aria-live="polite" aria-atomic="true">
        {state.pending && <p>Saving…</p>}
        {!state.pending && state.loading && <p>Loading…</p>}
        {state.error && <p role="alert">{state.error}</p>}
        {state.error && !state.loaded && (
          <button type="button" onClick={() => void state.refresh()}>
            Retry
          </button>
        )}
      </div>
      {pickerOpen && (
        <FolderPicker
          anchor={buttonRef}
          initialPath={workingDirectory}
          onChoose={(path) => void state.save(path)}
          onClose={(returnFocus) => {
            setPickerOpen(false);
            if (returnFocus) buttonRef.current?.focus({ preventScroll: true });
          }}
        />
      )}
    </section>
  );
}
