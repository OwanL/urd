import type { ProviderConnection } from "../ProviderConnection.ts";
import type { ConnectionDraft, ProviderSettingsSnapshot } from "./ProviderSettingsTypes.ts";

export type ConnectionReadState = Pick<
  ProviderSettingsSnapshot,
  | "connections"
  | "drafts"
  | "selectedId"
  | "loaded"
  | "uncertain"
  | "credentials"
  | "catalogs"
  | "modelSettings"
  | "modelDrafts"
  | "errors"
  | "signIns"
>;

function draftOf(connection: ProviderConnection): ConnectionDraft {
  return {
    name: connection.name,
    endpoint: connection.endpoint,
    api: connection.api === "openAIChatCompletions" ? "openAIChatCompletions" : "openAIResponses",
    authentication: connection.authentication === "none" ? "none" : "apiKey",
  };
}

function savedDraft(
  snapshot: Pick<ConnectionReadState, "connections" | "drafts">,
  connection: ProviderConnection,
): ConnectionDraft {
  const draft = snapshot.drafts[connection.id];
  const previous = snapshot.connections.find((item) => item.id === connection.id);
  if (!draft || !previous) return draftOf(connection);
  const saved = draftOf(previous);
  const edited = Object.keys(saved).some(
    (key) => draft[key as keyof ConnectionDraft] !== saved[key as keyof ConnectionDraft],
  );
  return edited ? draft : draftOf(connection);
}

function retainIds<T>(records: Readonly<Record<string, T>>, ids: ReadonlySet<string>) {
  return Object.fromEntries(Object.entries(records).filter(([id]) => ids.has(id)));
}

export function reconcileConnections(
  snapshot: ConnectionReadState,
  connections: readonly ProviderConnection[],
  uncertainModelConnectionId: string | null,
) {
  const drafts = Object.fromEntries(
    connections.map((connection) => [connection.id, savedDraft(snapshot, connection)]),
  );
  const ids = new Set(connections.map((connection) => connection.id));
  let selectedId = snapshot.selectedId;
  if (!snapshot.loaded || (selectedId && !ids.has(selectedId)))
    selectedId = connections[0]?.id ?? null;
  return {
    connections,
    drafts,
    selectedId,
    loaded: true,
    connectionsReloadedAfterUncertainty:
      snapshot.uncertain &&
      (uncertainModelConnectionId === null || !ids.has(uncertainModelConnectionId)),
    credentials: retainIds(snapshot.credentials, ids),
    catalogs: retainIds(snapshot.catalogs, ids),
    modelSettings: retainIds(snapshot.modelSettings, ids),
    modelDrafts: retainIds(snapshot.modelDrafts, ids),
    errors: Object.fromEntries(
      Object.entries(snapshot.errors).filter(
        ([key]) => key === "connections" || ids.has(key.slice(key.indexOf(":") + 1)),
      ),
    ),
    signIns: retainIds(snapshot.signIns, ids),
    ...(selectedId !== snapshot.selectedId ? { apiKey: "", confirmation: null } : {}),
  };
}

export function recordConnection(
  snapshot: Pick<ProviderSettingsSnapshot, "connections" | "drafts">,
  connection: ProviderConnection,
) {
  const exists = snapshot.connections.some((item) => item.id === connection.id);
  return {
    connections: exists
      ? snapshot.connections.map((item) => (item.id === connection.id ? connection : item))
      : [...snapshot.connections, connection],
    drafts: { ...snapshot.drafts, [connection.id]: draftOf(connection) },
  };
}

export function removeConnection(
  snapshot: Pick<
    ProviderSettingsSnapshot,
    "drafts" | "modelSettings" | "modelDrafts" | "selectedId"
  >,
  id: string,
  connections: readonly ProviderConnection[],
) {
  const drafts = { ...snapshot.drafts };
  const modelSettings = { ...snapshot.modelSettings };
  const modelDrafts = { ...snapshot.modelDrafts };
  delete drafts[id];
  delete modelSettings[id];
  delete modelDrafts[id];
  return {
    connections,
    drafts,
    modelSettings,
    modelDrafts,
    selectedId: snapshot.selectedId === id ? (connections[0]?.id ?? null) : snapshot.selectedId,
  };
}

export function forgetConnectionStatus(
  snapshot: Pick<ProviderSettingsSnapshot, "credentials" | "catalogs" | "signIns">,
  id: string,
) {
  const credentials = { ...snapshot.credentials };
  const catalogs = { ...snapshot.catalogs };
  const signIns = { ...snapshot.signIns };
  delete credentials[id];
  delete catalogs[id];
  delete signIns[id];
  return { credentials, catalogs, signIns };
}
