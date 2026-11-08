import type { RefObject } from "preact";
import { useId, useRef, useState } from "preact/hooks";
import { Icon } from "../components/Icon.tsx";
import { IconButton } from "../components/IconButton.tsx";
import { Popover } from "../components/Popover.tsx";
import type { ProviderClient } from "../providers/ProviderClient.ts";
import { ProviderSettings, useProviderSettings } from "../providers/settings/index.ts";
import "./settings.css";

interface SettingsProps {
  open: boolean;
  anchor: RefObject<HTMLElement | null>;
  client: ProviderClient;
  onClose: (returnFocus: boolean) => void;
  onChanged: () => void;
}

// Keep this owner mounted; closing the surface stops reads, not accepted commands.
export function Settings({ open, anchor, client, onClose, onChanged }: SettingsProps) {
  const state = useProviderSettings(client, open, onChanged);
  const [search, setSearch] = useState("");
  const searchInput = useRef<HTMLInputElement>(null);
  const id = useId();
  if (!open) return null;

  const close = (returnFocus: boolean) => {
    setSearch("");
    if (returnFocus) anchor.current?.focus({ preventScroll: true });
    onClose(returnFocus);
  };
  return (
    <Popover
      anchor={anchor}
      class="settings-popover"
      label="Settings"
      placement="below"
      initialFocus={searchInput}
      onClose={close}
    >
      <header class="settings-heading">
        <h2>Settings</h2>
        <IconButton icon="close" label="Close settings" onClick={() => close(true)} />
      </header>
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
      <div class="settings-tabs" role="tablist" aria-label="Settings categories">
        <button
          type="button"
          id={`${id}-providers-tab`}
          role="tab"
          aria-selected="true"
          aria-controls={`${id}-providers-panel`}
        >
          Providers
        </button>
      </div>
      <section
        class="settings-page"
        id={`${id}-providers-panel`}
        role="tabpanel"
        aria-labelledby={`${id}-providers-tab`}
      >
        <ProviderSettings state={state} search={search} />
      </section>
    </Popover>
  );
}
