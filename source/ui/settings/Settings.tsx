import type { TargetedKeyboardEvent } from "preact";
import type { RefObject } from "preact";
import { useEffect, useId, useRef, useState } from "preact/hooks";
import { Icon } from "../components/Icon.tsx";
import { IconButton } from "../components/IconButton.tsx";
import { Popover } from "../components/Popover.tsx";
import {
  compactionTriggerInput,
  reconcileCompactionTriggerDraft,
} from "../preferences/CompactionTriggerInput.ts";
import type { PreferencesState } from "../preferences/usePreferences.ts";
import type { ProviderClient } from "../providers/ProviderClient.ts";
import { ProviderSettings, useProviderSettings } from "../providers/settings/index.ts";
import { GeneralSettings } from "./GeneralSettings.tsx";
import "./settings.css";

export type SettingsTab = "providers" | "general";

interface SettingsProps {
  open: boolean;
  anchor: RefObject<HTMLElement | null>;
  client: ProviderClient;
  preferences: PreferencesState;
  tab: SettingsTab;
  onTabChange: (tab: SettingsTab) => void;
  onClose: (returnFocus: boolean) => void;
  onChanged: () => void;
}

// Keep this owner mounted; closing the surface stops reads, not accepted commands.
export function Settings({
  open,
  anchor,
  client,
  preferences,
  tab,
  onTabChange,
  onClose,
  onChanged,
}: SettingsProps) {
  const state = useProviderSettings(client, open, onChanged);
  const [search, setSearch] = useState("");
  const savedCompactionTriggerTokenCount =
    preferences.preferences?.compactionTriggerTokenCount ?? null;
  const [compactionTriggerDraft, setCompactionTriggerDraft] = useState(() => ({
    value: compactionTriggerInput(savedCompactionTriggerTokenCount),
    edited: false,
  }));
  useEffect(() => {
    setCompactionTriggerDraft((current) =>
      reconcileCompactionTriggerDraft(
        current.value,
        current.edited,
        savedCompactionTriggerTokenCount,
      ),
    );
  }, [savedCompactionTriggerTokenCount]);
  const searchInput = useRef<HTMLInputElement>(null);
  const workingDirectoryButton = useRef<HTMLButtonElement>(null);
  const providersTab = useRef<HTMLButtonElement>(null);
  const generalTab = useRef<HTMLButtonElement>(null);
  const id = useId();
  if (!open) return null;

  const close = (returnFocus: boolean) => {
    setSearch("");
    if (returnFocus) anchor.current?.focus({ preventScroll: true });
    onClose(returnFocus);
  };
  const focusTab = (next: SettingsTab) => {
    onTabChange(next);
    (next === "providers" ? providersTab : generalTab).current?.focus();
  };
  const navigateTabs = (event: TargetedKeyboardEvent<HTMLDivElement>) => {
    if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
      event.preventDefault();
      focusTab(tab === "providers" ? "general" : "providers");
    } else if (event.key === "Home" || event.key === "End") {
      event.preventDefault();
      focusTab(event.key === "Home" ? "providers" : "general");
    }
  };
  return (
    <Popover
      anchor={anchor}
      class="settings-popover"
      label="Settings"
      placement="below"
      initialFocus={tab === "providers" ? searchInput : workingDirectoryButton}
      onClose={close}
    >
      <header class="settings-heading">
        <h2>Settings</h2>
        <IconButton icon="close" label="Close settings" onClick={() => close(true)} />
      </header>
      <div
        class="settings-tabs"
        role="tablist"
        aria-label="Settings categories"
        onKeyDown={navigateTabs}
      >
        <button
          ref={providersTab}
          type="button"
          id={`${id}-providers-tab`}
          role="tab"
          aria-selected={tab === "providers"}
          aria-controls={`${id}-providers-panel`}
          tabIndex={tab === "providers" ? 0 : -1}
          onClick={() => onTabChange("providers")}
        >
          Providers
        </button>
        <button
          ref={generalTab}
          type="button"
          id={`${id}-general-tab`}
          role="tab"
          aria-selected={tab === "general"}
          aria-controls={`${id}-general-panel`}
          tabIndex={tab === "general" ? 0 : -1}
          onClick={() => onTabChange("general")}
        >
          General
        </button>
      </div>
      <section
        class="settings-page"
        id={`${id}-providers-panel`}
        role="tabpanel"
        aria-labelledby={`${id}-providers-tab`}
        hidden={tab !== "providers"}
      >
        <label class="settings-search">
          <Icon name="search" />
          <input
            ref={searchInput}
            type="search"
            aria-label="Search providers"
            placeholder="Search providers…"
            value={search}
            onInput={(event) => setSearch(event.currentTarget.value)}
          />
        </label>
        {tab === "providers" && <ProviderSettings state={state} search={search} />}
      </section>
      <section
        class="settings-page"
        id={`${id}-general-panel`}
        role="tabpanel"
        aria-labelledby={`${id}-general-tab`}
        hidden={tab !== "general"}
      >
        <GeneralSettings
          state={preferences}
          buttonRef={workingDirectoryButton}
          compactionTriggerDraft={compactionTriggerDraft.value}
          onCompactionTriggerDraftChange={(value) =>
            setCompactionTriggerDraft(
              reconcileCompactionTriggerDraft(value, true, savedCompactionTriggerTokenCount),
            )
          }
          onCompactionTriggerSaved={(value) =>
            setCompactionTriggerDraft({ value: compactionTriggerInput(value), edited: false })
          }
        />
      </section>
    </Popover>
  );
}
