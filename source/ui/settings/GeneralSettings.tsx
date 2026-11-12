import type { RefObject } from "preact";
import { useEffect, useRef, useState } from "preact/hooks";
import type { WorkingDirectoryPreferenceState } from "../preferences/useWorkingDirectoryPreferences.ts";

interface GeneralSettingsProps {
  state: WorkingDirectoryPreferenceState;
  inputRef: RefObject<HTMLInputElement | null>;
}

export function GeneralSettings({ state, inputRef }: GeneralSettingsProps) {
  const [workingDirectory, setWorkingDirectory] = useState("");
  const edited = useRef(false);
  const draftVersion = useRef(0);

  useEffect(() => {
    if (state.loaded && !edited.current)
      setWorkingDirectory(state.preferences?.workingDirectory ?? "");
  }, [state.loaded, state.preferences?.workingDirectory]);

  const editWorkingDirectory = (value: string) => {
    edited.current = true;
    draftVersion.current++;
    setWorkingDirectory(value);
  };
  const saveWorkingDirectory = async (value: string | null) => {
    const version = draftVersion.current;
    if ((await state.save(value)) && version === draftVersion.current) {
      edited.current = false;
      setWorkingDirectory(value ?? "");
    }
  };

  return (
    <section
      class="general-settings"
      aria-label="General"
      aria-busy={state.loading || state.pending}
    >
      <form
        onSubmit={(event) => {
          event.preventDefault();
          if (workingDirectory.length > 0) void saveWorkingDirectory(workingDirectory);
        }}
      >
        <label class="general-settings-field">
          Working directory
          <input
            ref={inputRef}
            type="text"
            aria-label="Working directory"
            autoComplete="off"
            value={workingDirectory}
            onInput={(event) => editWorkingDirectory(event.currentTarget.value)}
          />
        </label>
        <div class="general-settings-actions">
          <button type="submit" disabled={state.pending || workingDirectory.length === 0}>
            Save
          </button>
          <button
            type="button"
            disabled={state.pending || state.preferences?.workingDirectory === null}
            onClick={() => void saveWorkingDirectory(null)}
          >
            Clear
          </button>
        </div>
      </form>
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
    </section>
  );
}
