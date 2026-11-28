import type { ProviderConnection } from "../ProviderConnection.ts";
import { recordConnection, removeConnection } from "./ProviderConnectionRecords.ts";
import { providerHostname } from "./ProviderConnectionStatus.ts";
import type { RunProviderCommand } from "./ProviderSettingsCommand.ts";
import type {
  Confirmation,
  ProviderSettingsClient,
  ProviderSettingsSnapshot,
} from "./ProviderSettingsTypes.ts";

type ConnectionState = Pick<
  ProviderSettingsSnapshot,
  "connections" | "drafts" | "add" | "selectedId" | "modelSettings" | "modelDrafts"
>;

// Applies connection commands through the controller's admission and publication boundaries.
export function connectionCommands(
  client: Pick<
    ProviderSettingsClient,
    | "createChatGpt"
    | "createGitHubCopilot"
    | "createOpenAI"
    | "createCustom"
    | "updateCustom"
    | "remove"
    | "disconnect"
  >,
  command: RunProviderCommand,
  snapshot: () => ConnectionState,
  publish: (update: Partial<ConnectionState> & { confirmation?: Confirmation }) => void,
  forgetStatus: (id: string) => void,
) {
  async function create(created: (connection: ProviderConnection) => boolean): Promise<void> {
    const { choice, ...draft } = snapshot().add;
    await command(
      "add",
      () => {
        if (choice === "chatGpt") return client.createChatGpt("api.openai.com");
        if (choice === "gitHubCopilot") return client.createGitHubCopilot("GitHub Copilot");
        if (choice === "openAI") return client.createOpenAI("api.openai.com");
        return client.createCustom({
          ...draft,
          name: choice === "ollama" ? "Ollama" : providerHostname(draft.endpoint),
        });
      },
      (connection) => {
        publish(recordConnection(snapshot(), connection));
        if (snapshot().selectedId === null) publish({ selectedId: connection.id });
      },
      { afterSuccess: created },
    );
  }

  async function save(connection: ProviderConnection): Promise<void> {
    if (connection.route !== "custom") return;
    const draft = snapshot().drafts[connection.id];
    if (!draft) return;
    const name =
      connection.name === providerHostname(connection.endpoint)
        ? providerHostname(draft.endpoint)
        : connection.name;
    const settings = { ...draft, name };
    const bindingChanged =
      settings.endpoint !== connection.endpoint ||
      settings.api !== connection.api ||
      settings.authentication !== connection.authentication;
    if (bindingChanged) {
      publish({ confirmation: { kind: "binding", connection, settings } });
      return;
    }
    await command(
      "save",
      () => client.updateCustom(connection.id, settings),
      (updated) => publish(recordConnection(snapshot(), updated)),
    );
  }

  async function confirm({ connection, kind, settings }: Confirmation): Promise<void> {
    const { id } = connection;
    if (kind === "binding" && settings) {
      await command(
        "changeBinding",
        () => client.updateCustom(id, settings),
        (updated) => {
          publish(recordConnection(snapshot(), updated));
          forgetStatus(id);
        },
      );
    } else if (kind === "remove") {
      await command(
        "remove",
        () => client.remove(id),
        () => {
          const connections = snapshot().connections.filter((item) => item.id !== id);
          forgetStatus(id);
          publish(removeConnection(snapshot(), id, connections));
        },
      );
    } else if (kind === "disconnect") {
      await command(
        "disconnect",
        () => client.disconnect(id),
        (result) => {
          forgetStatus(id);
          return result;
        },
      );
    }
  }

  return { create, save, confirm };
}
