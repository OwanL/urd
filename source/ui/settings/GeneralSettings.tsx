import type { RefObject } from "preact";
import { useId } from "preact/hooks";
import type { PreferencesState } from "../preferences/usePreferences.ts";
import type { ProviderSettingsState } from "../providers/settings/useProviderSettings.ts";
import { CompactionSetting } from "./CompactionSetting.tsx";
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
  const id = useId();
  return (
    <section
      class="general-settings"
      aria-label="General"
      aria-busy={state.loading || state.pending}
    >
      <WorkingDirectorySetting state={state} buttonRef={buttonRef} />
      <CompactionSetting
        state={state}
        draft={compactionTriggerDraft}
        onDraftChange={onCompactionTriggerDraftChange}
        onSaved={onCompactionTriggerSaved}
      />
      <label class="attention-sounds-setting">
        <input
          type="checkbox"
          checked={attentionSounds}
          onChange={(event) => onAttentionSoundsChange(event.currentTarget.checked)}
        />
        Play question and completion sounds
      </label>
      <section class="general-settings-group" aria-labelledby={`${id}-subagents`}>
        <h3 id={`${id}-subagents`}>Subagents</h3>
        <MaxSubagentDepth state={state} />
        <DefaultSubagentProviders providers={providers} state={state} />
      </section>
      <SourceSettings active={active} />
      {location.hostname === "127.0.0.1" && (
        <section
          class="general-settings-group host-browser-settings"
          aria-labelledby={`${id}-host`}
        >
          <h3 id={`${id}-host`}>Host &amp; browser</h3>
          <LanHostingSetting />
          <BitwardenSetting />
        </section>
      )}
    </section>
  );
}
