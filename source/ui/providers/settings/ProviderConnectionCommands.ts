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
    const { route, ...draft } = snapshot().add;
    const settings = {
      ...draft,
      name: providerHostname(route === "custom" ? draft.endpoint : "https://api.openai.com"),
    };
    await command(
      "add",
      () => {
        if (route === "chatGpt") return client.createChatGpt(settings.name);
        if (route === "gitHubCopilot") return client.createGitHubCopilot("GitHub Copilot");
        if (route === "openAI") return client.createOpenAI(settings.name);
        return client.createCustom(settings);
      },
      (connection) => {
        publish(recordConnection(snapshot(), connection));
        const { selectedId, add } = snapshot();
        publish({
          ...(selectedId === null ? { selectedId: connection.id } : {}),
          add: { ...add, name: "" },
        });
      },
      { afterSuccess: created },
    );
  }

  async function save(connection: ProviderConnection): Promise<void> {
    if (connection.route !== "custom") return;
    const draft = snapshot().drafts[connection.id];
    if (!draft) return;
    const settings = { ...draft, name: providerHostname(draft.endpoint) };
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
