import type { TargetedKeyboardEvent } from "preact";
import type { RefObject } from "preact";
import { useEffect, useId, useRef, useState } from "preact/hooks";
import { McpSettings } from "../mcp/McpSettings.tsx";
import { useMcpSettings } from "../mcp/useMcpSettings.ts";
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
import { ModelPreferencesSettings } from "./ModelPreferencesSettings.tsx";
import { ProtectedResourcesSettings } from "./ProtectedResourcesSettings.tsx";
import "./settings.css";

export type SettingsTab = "providers" | "general" | "model-buckets" | "prepass" | "mcp";

const tabs: readonly SettingsTab[] = ["providers", "general", "model-buckets", "prepass", "mcp"];
const tabLabels: Record<SettingsTab, string> = {
  providers: "Providers",
  general: "General",
  "model-buckets": "Model buckets",
  prepass: "Prepass",
  mcp: "MCP",
};

interface SettingsProps {
  open: boolean;
  anchor: RefObject<HTMLElement | null>;
  client: ProviderClient;
  preferences: PreferencesState;
  tab: SettingsTab;
  onTabChange: (tab: SettingsTab) => void;
  onClose: (returnFocus: boolean) => void;
  onChanged: () => void;
  attentionSounds: boolean;
  onAttentionSoundsChange: (enabled: boolean) => void;
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
  attentionSounds,
  onAttentionSoundsChange,
  onProviderReady,
}: SettingsProps) {
  const mcp = useMcpSettings(open && tab === "mcp");
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
  const tabButtons = useRef<Partial<Record<SettingsTab, HTMLButtonElement | null>>>({});
  const id = useId();
  if (!open) return null;

  const close = (returnFocus: boolean) => {
    setSearch("");
    if (returnFocus) anchor.current?.focus({ preventScroll: true });
    onClose(returnFocus);
  };
  const focusTab = (next: SettingsTab) => {
    onTabChange(next);
    tabButtons.current[next]?.focus();
  };
  const navigateTabs = (event: TargetedKeyboardEvent<HTMLDivElement>) => {
    if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
      event.preventDefault();
      const direction = event.key === "ArrowRight" ? 1 : -1;
      focusTab(tabs[(tabs.indexOf(tab) + direction + tabs.length) % tabs.length] ?? "providers");
    } else if (event.key === "Home" || event.key === "End") {
      event.preventDefault();
      focusTab(event.key === "Home" ? "providers" : "mcp");
    }
  };
  return (
    <Popover
      anchor={anchor}
      class="settings-popover"
      label="Settings"
      placement="below"
      initialFocus={{
        get current() {
          const field =
            tab === "providers"
              ? searchInput.current
              : tab === "general"
                ? workingDirectoryButton.current
                : null;
          return field ?? tabButtons.current[tab] ?? null;
        },
      }}
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
        {tabs.map((category) => (
          <button
            key={category}
            ref={(button) => {
              tabButtons.current[category] = button;
            }}
            type="button"
            id={`${id}-${category}-tab`}
            role="tab"
            aria-selected={tab === category}
            aria-controls={`${id}-${category}-panel`}
            tabIndex={tab === category ? 0 : -1}
            onClick={() => onTabChange(category)}
          >
            {tabLabels[category]}
          </button>
        ))}
      </div>
      <section
        class="settings-page"
        id={`${id}-mcp-panel`}
        role="tabpanel"
        aria-labelledby={`${id}-mcp-tab`}
        hidden={tab !== "mcp"}
      >
        {tab === "mcp" && <McpSettings state={mcp} />}
      </section>
      <section
        class="settings-page"
        id={`${id}-providers-panel`}
        role="tabpanel"
        aria-labelledby={`${id}-providers-tab`}
        hidden={tab !== "providers"}
      >
        {tab === "providers" && (
          <ProviderSettings
            state={state}
            search={{ value: search, onChange: setSearch, inputRef: searchInput }}
          />
        )}
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
          providers={state}
          active={tab === "general"}
          buttonRef={workingDirectoryButton}
          attentionSounds={attentionSounds}
          onAttentionSoundsChange={onAttentionSoundsChange}
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
          <ModelPreferencesSettings
            client={client}
            state={preferences}
            view="buckets"
            active={tab === "model-buckets"}
          />
        </div>
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
