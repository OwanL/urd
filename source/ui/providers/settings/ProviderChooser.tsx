import { Icon } from "../../controls/Icon.tsx";
import type { ProviderConnection, ProviderRoute } from "../ProviderConnection.ts";
import type { ProviderSettingsState } from "./useProviderSettings.ts";
import { connectionStatus, connectionTitle } from "./ProviderSettingsPresentation.ts";

const choices = [
  {
    route: "chatGpt",
    label: "Sign in with ChatGPT",
    hint: "Use the Codex access included in your ChatGPT plan.",
  },
  {
    route: "openAI",
    label: "Use an OpenAI API key",
    hint: "Pay for API usage separately from your ChatGPT plan.",
  },
  {
    route: "custom",
    label: "Custom endpoint",
    hint: "Connect a local server or another OpenAI-compatible URL.",
  },
] as const;

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
        {choices.map(({ route, label, hint }) => (
          <li key={route}>
            <button
              type="button"
              class="provider-settings-connection provider-choice"
              disabled={!canEdit}
              onClick={() => onChoose(route)}
            >
              <span class="provider-choice-copy">
                <strong>{label}</strong>
                <span class="provider-settings-secondary">{hint}</span>
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
