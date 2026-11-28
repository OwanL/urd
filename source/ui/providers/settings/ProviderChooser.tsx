import { Icon } from "../../controls/Icon.tsx";
import type { ProviderConnection } from "../ProviderConnection.ts";
import { providerLabel } from "../ProviderLabel.ts";
import type { ProviderChoice } from "./ProviderSettingsTypes.ts";
import type { ProviderSettingsState } from "./useProviderSettings.ts";
import { connectionStatus } from "./ProviderSettingsPresentation.ts";

const choices = [
  {
    choice: "chatGpt",
    label: "Sign in with ChatGPT",
    hint: "Use the Codex access included in your ChatGPT plan.",
  },
  {
    choice: "gitHubCopilot",
    label: "Sign in with GitHub Copilot",
    hint: "Use Copilot access provided by your GitHub account or organization.",
  },
  {
    choice: "openAI",
    label: "Use an OpenAI API key",
    hint: "Pay for API usage separately from your ChatGPT plan.",
  },
  {
    choice: "ollama",
    label: "Ollama",
    hint: "Connect to a running Ollama server.",
  },
  {
    choice: "custom",
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
  onRemoveConnection,
}: {
  state: ProviderSettingsState;
  unfinished: readonly ProviderConnection[];
  unknown: readonly ProviderConnection[];
  canEdit: boolean;
  canNavigate: boolean;
  onChoose: (choice: ProviderChoice) => void;
  onOpenConnection: (connection: ProviderConnection, setup?: boolean) => void;
  onRemoveConnection: (connection: ProviderConnection) => void;
}) {
  return (
    <>
      <ul class="provider-settings-connections" aria-label="Choose a provider">
        {choices.map(({ choice, label, hint }) => (
          <li key={choice}>
            <button
              type="button"
              class="provider-settings-connection provider-choice"
              disabled={!canEdit}
              onClick={() => onChoose(choice)}
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
                <li key={item.id} class="provider-settings-setup-row">
                  <button
                    type="button"
                    class="provider-settings-connection"
                    disabled={!canNavigate}
                    onClick={() => onOpenConnection(item, true)}
                  >
                    <strong>{providerLabel(item)}</strong>
                    <span class="provider-settings-secondary">{connectionStatus(state, item)}</span>
                    <Icon name="chevron" />
                  </button>
                  <button
                    type="button"
                    class="provider-settings-remove-setup provider-settings-danger"
                    disabled={!canEdit}
                    aria-label={`Remove saved setup for ${providerLabel(item)}`}
                    onClick={() => onRemoveConnection(item)}
                  >
                    Remove
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
