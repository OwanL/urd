import type { TargetedKeyboardEvent } from "preact";
import type { RefObject } from "preact";
import { useEffect, useId, useRef, useState } from "preact/hooks";
import { Icon } from "../controls/Icon.tsx";
import { IconButton } from "../controls/IconButton.tsx";
import { Popover } from "../controls/Popover.tsx";
import {
  compactionTriggerInput,
  reconcileCompactionTriggerDraft,
} from "../preferences/CompactionTriggerInput.ts";
import type { PreferencesState } from "../preferences/usePreferences.ts";
import type { ProviderClient } from "../providers/ProviderClient.ts";
import { ProviderSettings } from "../providers/settings/ProviderSettings.tsx";
import { useProviderSettings } from "../providers/settings/useProviderSettings.ts";
import { GeneralSettings } from "./GeneralSettings.tsx";
import { DefaultSubagentProviders } from "./DefaultSubagentProviders.tsx";
import { ModelPreferencesSettings } from "./ModelPreferencesSettings.tsx";
import { ModelBucketsPanel } from "./ModelBucketsPanel.tsx";
import { ProtectedResourcesSettings } from "./ProtectedResourcesSettings.tsx";
import "./settings.css";

export type SettingsTab = "providers" | "general" | "model-buckets" | "prepass";

const tabs: readonly SettingsTab[] = ["providers", "general", "model-buckets", "prepass"];

interface SettingsProps {
  open: boolean;
  anchor: RefObject<HTMLElement | null>;
  client: ProviderClient;
  preferences: PreferencesState;
  tab: SettingsTab;
  onTabChange: (tab: SettingsTab) => void;
  onClose: (returnFocus: boolean) => void;
  onChanged: () => void;
  onProviderReady?: (connectionId: string) => Promise<boolean>;
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
  onProviderReady,
}: SettingsProps) {
  const state = useProviderSettings(
    client,
    open,
    onChanged,
    tab === "providers",
    onProviderReady,
    preferences.refresh,
  );
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
  const modelBucketsTab = useRef<HTMLButtonElement>(null);
  const prepassTab = useRef<HTMLButtonElement>(null);
  const id = useId();
  if (!open) return null;

  const close = (returnFocus: boolean) => {
    setSearch("");
    if (returnFocus) anchor.current?.focus({ preventScroll: true });
    onClose(returnFocus);
  };
  const focusTab = (next: SettingsTab) => {
    onTabChange(next);
    (next === "providers"
      ? providersTab
      : next === "general"
        ? generalTab
        : next === "model-buckets"
          ? modelBucketsTab
          : prepassTab
    ).current?.focus();
  };
  const navigateTabs = (event: TargetedKeyboardEvent<HTMLDivElement>) => {
    if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
      event.preventDefault();
      const direction = event.key === "ArrowRight" ? 1 : -1;
      focusTab(tabs[(tabs.indexOf(tab) + direction + tabs.length) % tabs.length] ?? "providers");
    } else if (event.key === "Home" || event.key === "End") {
      event.preventDefault();
      focusTab(event.key === "Home" ? "providers" : "prepass");
    }
  };
  return (
    <Popover
      anchor={anchor}
      class="settings-popover"
      label="Settings"
      placement="below"
      initialFocus={
        tab === "providers"
          ? searchInput
          : tab === "general"
            ? workingDirectoryButton
            : tab === "model-buckets"
              ? modelBucketsTab
              : prepassTab
      }
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
        <button
          ref={modelBucketsTab}
          type="button"
          id={`${id}-model-buckets-tab`}
          role="tab"
          aria-selected={tab === "model-buckets"}
          aria-controls={`${id}-model-buckets-panel`}
          tabIndex={tab === "model-buckets" ? 0 : -1}
          onClick={() => onTabChange("model-buckets")}
        >
          Model buckets
        </button>
        <button
          ref={prepassTab}
          type="button"
          id={`${id}-prepass-tab`}
          role="tab"
          aria-selected={tab === "prepass"}
          aria-controls={`${id}-prepass-panel`}
          tabIndex={tab === "prepass" ? 0 : -1}
          onClick={() => onTabChange("prepass")}
        >
          Prepass
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
      <section
        class="settings-page"
        id={`${id}-model-buckets-panel`}
        role="tabpanel"
        aria-labelledby={`${id}-model-buckets-tab`}
        hidden={tab !== "model-buckets"}
      >
        <div class="general-settings">
          <ModelBucketsPanel client={client} state={preferences} active={tab === "model-buckets"} />
        </div>
        {tab === "model-buckets" && (
          <DefaultSubagentProviders providers={state} state={preferences} />
        )}
      </section>
      <section
        class="settings-page"
        id={`${id}-prepass-panel`}
        role="tabpanel"
        aria-labelledby={`${id}-prepass-tab`}
        hidden={tab !== "prepass"}
      >
        <div class="general-settings">
          <ModelPreferencesSettings
            client={client}
            state={preferences}
            view="purposes"
            active={tab === "prepass"}
          />
          <ProtectedResourcesSettings state={preferences} active={tab === "prepass"} />
        </div>
      </section>
    </Popover>
  );
}
