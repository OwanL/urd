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
        <code class="general-settings-path">{workingDirectory ?? "…"}</code>
        <p class="general-settings-description">Used for new chats.</p>
      </div>
      <div class="general-settings-actions">
        <button
          ref={buttonRef}
          type="button"
          disabled={state.pending}
          onClick={() => setPickerOpen(true)}
        >
          Edit…
        </button>
        <button
          type="button"
          disabled={state.pending || !state.loaded || workingDirectory === null}
          onClick={() => void state.save(null)}
        >
          Reset to root
        </button>
      </div>
      {!pickerOpen && (
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
      )}
      {pickerOpen && (
        <FolderPicker
          anchor={buttonRef}
          title="Default working directory"
          initialPath={workingDirectory}
          busy={state.pending}
          feedback={state.error && <p role="alert">{state.error}</p>}
          onChoose={state.save}
          onClose={(returnFocus) => {
            setPickerOpen(false);
            if (returnFocus) buttonRef.current?.focus({ preventScroll: true });
          }}
        />
      )}
    </section>
  );
}
