import type { RefObject } from "preact";
import { useState } from "preact/hooks";
import { FolderPicker } from "../folder-browser/FolderPicker.tsx";
import type { PreferencesState } from "../preferences/usePreferences.ts";

/** The default working directory control shared by Settings and first-run setup. */
export function WorkingDirectorySetting({
  state,
  buttonRef,
  emptyLabel = "…",
  resettable = true,
  chosen = true,
  onChoose = state.saveWorkingDirectory,
}: {
  state: PreferencesState;
  buttonRef: RefObject<HTMLButtonElement | null>;
  emptyLabel?: string;
  resettable?: boolean;
  /** False while the value is only the server's fallback rather than a saved choice. */
  chosen?: boolean;
  onChoose?(path: string): Promise<boolean>;
}) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const workingDirectory = state.preferences?.workingDirectory ?? null;
  const shown = chosen ? workingDirectory : null;
  const error = state.errorFor("workingDirectory");

  return (
    <>
      <div class="general-settings-field">
        <span>Working directory</span>
        <code class="general-settings-path">{shown ?? emptyLabel}</code>
        <p class="general-settings-description">Used for new chats.</p>
      </div>
      <div class="general-settings-actions">
        <button
          ref={buttonRef}
          type="button"
          disabled={state.pending}
          onClick={() => {
            state.clearError();
            setPickerOpen(true);
          }}
        >
          {shown === null ? "Choose…" : "Edit…"}
        </button>
        {resettable && (
          <button
            type="button"
            disabled={state.pending || !state.loaded || workingDirectory === null}
            onClick={() => void state.saveWorkingDirectory(null)}
          >
            Reset to root
          </button>
        )}
      </div>
      {!pickerOpen && (
        <div class="general-settings-feedback" aria-live="polite" aria-atomic="true">
          {state.pendingFor === "workingDirectory" && <p>Saving…</p>}
          {!state.pending && state.loading && <p>Loading…</p>}
          {error && <p role="alert">{error}</p>}
          {error && !state.loaded && (
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
          feedback={error && <p role="alert">{error}</p>}
          onChoose={onChoose}
          onDraftChange={state.clearError}
          onClose={(returnFocus) => {
            setPickerOpen(false);
            if (returnFocus) buttonRef.current?.focus({ preventScroll: true });
          }}
        />
      )}
    </>
  );
}
