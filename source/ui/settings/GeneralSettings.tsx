import type { RefObject } from "preact";
import { useId, useState } from "preact/hooks";
import {
  compactionTriggerInput,
  parseCompactionTriggerInput,
} from "../preferences/CompactionTriggerInput.ts";
import type { PreferencesState } from "../preferences/usePreferences.ts";
import type { ProviderSettingsState } from "../providers/settings/useProviderSettings.ts";
import { DefaultSubagentProviders } from "./DefaultSubagentProviders.tsx";
import { MaxSubagentDepth } from "./MaxSubagentDepth.tsx";
import { SourceSettings } from "./SourceSettings.tsx";
import { WorkingDirectorySetting } from "./WorkingDirectorySetting.tsx";
import { LanHostingSetting } from "./LanHostingSetting.tsx";
import { BitwardenSetting } from "./BitwardenSetting.tsx";

interface GeneralSettingsProps {
  state: PreferencesState;
  providers: ProviderSettingsState;
  active: boolean;
  buttonRef: RefObject<HTMLButtonElement | null>;
  attentionSounds: boolean;
  onAttentionSoundsChange(enabled: boolean): void;
  compactionTriggerDraft: string;
  onCompactionTriggerDraftChange(value: string): void;
  onCompactionTriggerSaved(value: number | null): void;
}

export function GeneralSettings({
  state,
  providers,
  active,
  buttonRef,
  attentionSounds,
  onAttentionSoundsChange,
  compactionTriggerDraft,
  onCompactionTriggerDraftChange,
  onCompactionTriggerSaved,
}: GeneralSettingsProps) {
  const compactionTriggerTokenCount = state.preferences?.compactionTriggerTokenCount ?? null;
  // Load failures apply to every preference; the working directory field already reports them.
  const triggerError =
    state.errorFor("compactionTriggerTokenCount") === state.errorFor("workingDirectory")
      ? null
      : state.errorFor("compactionTriggerTokenCount");
  const [triggerValidation, setTriggerValidation] = useState<string | null>(null);
  const [triggerSaving, setTriggerSaving] = useState(false);
  const [triggerSaved, setTriggerSaved] = useState(false);
  const triggerId = useId();
  const triggerErrorId = `${triggerId}-error`;
  const triggerDisabled = !state.loaded || state.pending || triggerSaving;
  const triggerUnchanged =
    compactionTriggerDraft === compactionTriggerInput(compactionTriggerTokenCount);

  const editTrigger = (value: string) => {
    onCompactionTriggerDraftChange(value);
    setTriggerValidation(null);
    setTriggerSaved(false);
    state.clearError();
  };

  const saveTrigger = async () => {
    if (triggerDisabled || triggerUnchanged) return;
    const result = parseCompactionTriggerInput(compactionTriggerDraft);
    if (!result.valid) {
      setTriggerValidation(result.error);
      return;
    }

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
      <WorkingDirectorySetting state={state} buttonRef={buttonRef} />
      <form
        class="general-setting-form"
        aria-busy={triggerSaving}
        onSubmit={(event) => {
          event.preventDefault();
          void saveTrigger();
        }}
      >
        <label htmlFor={`${triggerId}-input`}>Compaction trigger (tokens)</label>
        <div class="settings-number-control">
          <input
            id={`${triggerId}-input`}
            type="text"
            inputMode="numeric"
            autoComplete="off"
            placeholder="Automatic"
            value={compactionTriggerDraft}
            aria-invalid={triggerValidation ? "true" : undefined}
            aria-describedby={triggerValidation ? triggerErrorId : undefined}
            disabled={triggerDisabled}
            onInput={(event) => editTrigger(event.currentTarget.value)}
          />
          <button
            type="button"
            disabled={
              triggerDisabled ||
              (compactionTriggerDraft === "" && compactionTriggerTokenCount === null)
            }
            onClick={() => editTrigger("")}
          >
            Set automatic
          </button>
          <button type="submit" disabled={triggerDisabled || triggerUnchanged}>
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
          {triggerError && <p role="alert">{triggerError}</p>}
        </div>
      </form>
      <label class="attention-sounds-setting">
        <input
          type="checkbox"
          checked={attentionSounds}
          onChange={(event) => onAttentionSoundsChange(event.currentTarget.checked)}
        />
        Play question and completion sounds
      </label>
      <section class="general-settings-group" aria-labelledby={`${triggerId}-subagents`}>
        <h3 id={`${triggerId}-subagents`}>Subagents</h3>
        <MaxSubagentDepth state={state} />
        <DefaultSubagentProviders providers={providers} state={state} />
      </section>
      <SourceSettings active={active} />
      {location.hostname === "127.0.0.1" && (
        <section
          class="general-settings-group host-browser-settings"
          aria-labelledby={`${triggerId}-host`}
        >
          <h3 id={`${triggerId}-host`}>Host &amp; browser</h3>
          <LanHostingSetting />
          <BitwardenSetting />
        </section>
      )}
    </section>
  );
}
