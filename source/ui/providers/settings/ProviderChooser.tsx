import { Icon } from "../../components/Icon.tsx";
import type { ProviderConnection, ProviderRoute } from "../ProviderConnection.ts";
import type { ProviderSettingsState } from "./useProviderSettings.ts";
import {
  connectionStatus,
  connectionTitle,
  providerNames,
} from "./ProviderSettingsPresentation.ts";

export function ProviderChooser({
  state,
  unfinished,
  unknown,
  canEdit,
  canNavigate,
  onChoose,
  onOpenConnection,
}: {
  state: ProviderSettingsState;
  unfinished: readonly ProviderConnection[];
  unknown: readonly ProviderConnection[];
  canEdit: boolean;
  canNavigate: boolean;
  onChoose: (route: ProviderRoute) => void;
  onOpenConnection: (connection: ProviderConnection, setup?: boolean) => void;
}) {
  return (
    <>
      <ul class="provider-settings-connections" aria-label="Choose a provider">
        {(Object.keys(providerNames) as ProviderRoute[]).map((route) => (
          <li key={route}>
            <button
              type="button"
              class="provider-settings-connection"
              disabled={!canEdit}
              onClick={() => onChoose(route)}
            >
              <strong>{providerNames[route]}</strong>
              <span class="provider-settings-secondary">
                {route === "chatGpt"
                  ? "Account sign-in"
                  : route === "openAI"
                    ? "API key"
                    : "Local or compatible server"}
              </span>
              <Icon name="chevron" />
            </button>
          </li>
        ))}
      </ul>
      {[
        { title: "Finish setup", items: unfinished },
        { title: "Check setup", items: unknown },
      ].map(({ title, items }) =>
        items.length ? (
          <section class="provider-settings-group" aria-label={title} key={title}>
            <h4>{title}</h4>
            <ul class="provider-settings-connections">
              {items.map((item) => (
                <li key={item.id}>
                  <button
                    type="button"
                    class="provider-settings-connection"
                    disabled={!canNavigate}
                    onClick={() => onOpenConnection(item, true)}
                  >
                    <strong>{connectionTitle(item)}</strong>
                    <span class="provider-settings-secondary">{connectionStatus(state, item)}</span>
                    <Icon name="chevron" />
                  </button>
                </li>
              ))}
            </ul>
          </section>
        ) : null,
      )}
    </>
  );
}
