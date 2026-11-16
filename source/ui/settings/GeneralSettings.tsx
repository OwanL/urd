import type { RefObject } from "preact";
import { useId, useState } from "preact/hooks";
import { FolderPicker } from "../components/FolderPicker.tsx";
import {
  compactionTriggerInput,
  parseCompactionTriggerInput,
} from "../preferences/CompactionTriggerInput.ts";
import type { PreferencesState } from "../preferences/usePreferences.ts";

interface GeneralSettingsProps {
  state: PreferencesState;
  buttonRef: RefObject<HTMLButtonElement | null>;
  compactionTriggerDraft: string;
  onCompactionTriggerDraftChange(value: string): void;
  onCompactionTriggerSaved(value: number | null): void;
}

export function GeneralSettings({
  state,
  buttonRef,
  compactionTriggerDraft,
  onCompactionTriggerDraftChange,
  onCompactionTriggerSaved,
}: GeneralSettingsProps) {
  const [pickerOpen, setPickerOpen] = useState(false);
  const workingDirectory = state.preferences?.workingDirectory ?? null;
  const workingDirectoryError = state.errorFor("workingDirectory");
  const compactionTriggerTokenCount = state.preferences?.compactionTriggerTokenCount ?? null;
  const generalError =
    state.errorFor("workingDirectory") ?? state.errorFor("compactionTriggerTokenCount");
  const [triggerValidation, setTriggerValidation] = useState<string | null>(null);
  const [triggerSaving, setTriggerSaving] = useState(false);
  const [triggerSaved, setTriggerSaved] = useState(false);
  const triggerId = useId();
  const triggerErrorId = `${triggerId}-error`;

  const editTrigger = (value: string) => {
    onCompactionTriggerDraftChange(value);
    setTriggerValidation(null);
    setTriggerSaved(false);
    state.clearError();
  };

  const saveTrigger = async () => {
    const result = parseCompactionTriggerInput(compactionTriggerDraft);
    if (!result.valid) {
      setTriggerValidation(result.error);
      return;
    }
    if (state.pending || triggerSaving) return;

    setTriggerValidation(null);
    setTriggerSaved(false);
    setTriggerSaving(true);
    if (await state.saveCompactionTriggerTokenCount(result.value)) {
      onCompactionTriggerSaved(result.value);
      setTriggerSaved(true);
    }
    setTriggerSaving(false);
  };

  return (
    <section
      class="general-settings"
      aria-label="General"
      aria-busy={state.loading || state.pending || triggerSaving}
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
          onClick={() => {
            state.clearError();
            setPickerOpen(true);
          }}
        >
          Edit…
        </button>
        <button
          type="button"
          disabled={state.pending || !state.loaded || workingDirectory === null}
          onClick={() => void state.saveWorkingDirectory(null)}
        >
          Reset to root
        </button>
      </div>
      <div class="general-settings-field compaction-trigger-setting" aria-busy={triggerSaving}>
        <label htmlFor={`${triggerId}-input`}>Compaction trigger (tokens)</label>
        <div class="compaction-trigger-control">
          <input
            id={`${triggerId}-input`}
            type="text"
            inputMode="numeric"
            autoComplete="off"
            placeholder="Automatic"
            value={compactionTriggerDraft}
            aria-invalid={triggerValidation ? "true" : undefined}
            aria-describedby={triggerValidation ? triggerErrorId : undefined}
            disabled={!state.loaded || state.pending || triggerSaving}
            onInput={(event) => editTrigger(event.currentTarget.value)}
          />
          <button
            type="button"
            disabled={
              !state.loaded ||
              state.pending ||
              triggerSaving ||
              (compactionTriggerDraft === "" && compactionTriggerTokenCount === null)
            }
            onClick={() => editTrigger("")}
          >
            Set automatic
          </button>
          <button
            type="button"
            disabled={
              !state.loaded ||
              state.pending ||
              triggerSaving ||
              compactionTriggerDraft === compactionTriggerInput(compactionTriggerTokenCount)
            }
            onClick={() => void saveTrigger()}
          >
            Save
          </button>
        </div>
        <div class="general-settings-feedback" aria-live="polite" aria-atomic="true">
          {triggerValidation && (
            <p id={triggerErrorId} role="alert">
              {triggerValidation}
            </p>
          )}
          {triggerSaving && <p role="status">Saving…</p>}
          {triggerSaved && !triggerSaving && <p role="status">Saved.</p>}
        </div>
      </div>
      {!pickerOpen && (
        <div class="general-settings-feedback" aria-live="polite" aria-atomic="true">
          {state.pending && !triggerSaving && <p>Saving…</p>}
          {!state.pending && state.loading && <p>Loading…</p>}
          {generalError && <p role="alert">{generalError}</p>}
          {generalError && !state.loaded && (
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
          feedback={workingDirectoryError && <p role="alert">{workingDirectoryError}</p>}
          onChoose={state.saveWorkingDirectory}
          onDraftChange={state.clearError}
          onClose={(returnFocus) => {
            setPickerOpen(false);
            if (returnFocus) buttonRef.current?.focus({ preventScroll: true });
          }}
        />
      )}
    </section>
  );
}
