import type { ModelSettings } from "../ModelSettings.ts";
import type { ProviderConnection } from "../ProviderConnection.ts";
import type { RunProviderCommand } from "./ProviderSettingsCommand.ts";
import type { ProviderSettingsClient, ProviderSettingsSnapshot } from "./ProviderSettingsTypes.ts";

type ModelState = Pick<ProviderSettingsSnapshot, "catalogs" | "modelSettings" | "modelDrafts">;

export type ModelSettingsEdit = Partial<
  Pick<ModelSettings, "family" | "supportsImages" | "contextWindow">
>;

// Model drafts are local; only explicit saves or refreshes dispatch commands.
export function modelCommands(
  client: Pick<ProviderSettingsClient, "saveModelSettings" | "refreshModels">,
  command: RunProviderCommand,
  snapshot: () => ModelState,
  publish: (update: Partial<ModelState>) => void,
) {
  function edit(settings: ModelSettings, update: ModelSettingsEdit): void {
    const { connectionId, modelId } = settings;
    const { modelSettings, modelDrafts } = snapshot();
    if (!modelSettings[connectionId]) return;
    const drafts = modelDrafts[connectionId] ?? [];
    const draft = drafts.find((item) => item.modelId === modelId) ?? settings;
    publish({
      modelDrafts: {
        ...modelDrafts,
        [connectionId]: [
          ...drafts.filter((item) => item.modelId !== modelId),
          { ...draft, ...update },
        ],
      },
    });
  }

  async function save(settings: ModelSettings): Promise<void> {
    if (!snapshot().modelSettings[settings.connectionId]) return;
    await command(
      "saveModelSettings",
      () => client.saveModelSettings(settings),
      (saved) => {
        const { connectionId, modelId } = saved;
        const { modelSettings } = snapshot();
        const records = modelSettings[connectionId] ?? [];
        publish({
          modelSettings: {
            ...modelSettings,
            [connectionId]: [...records.filter((item) => item.modelId !== modelId), saved],
          },
        });
        const { modelDrafts } = snapshot();
        publish({
          modelDrafts: {
            ...modelDrafts,
            [connectionId]: (modelDrafts[connectionId] ?? []).filter(
              (item) => item.modelId !== modelId,
            ),
          },
        });
      },
      { modelConnectionId: settings.connectionId },
    );
  }

  async function refresh(connection: ProviderConnection): Promise<void> {
    await command(
      "refreshModels",
      () => client.refreshModels(connection.id),
      (catalog) =>
        publish({ catalogs: { ...snapshot().catalogs, [catalog.connectionId]: catalog } }),
    );
  }

  return { edit, save, refresh };
}
