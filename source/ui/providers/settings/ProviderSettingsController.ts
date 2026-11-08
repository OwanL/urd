import { RequestError } from "../../http/RequestError.ts";
import type { ProviderClient } from "../ProviderClient.ts";
import type {
  CustomProviderSettings,
  ProviderConnection,
  ProviderRoute,
} from "../ProviderConnection.ts";
import type { ProviderCredentialStatus, ProviderDisconnectResult } from "../ProviderCredentials.ts";
import type { ProviderModelCatalog } from "../ProviderModels.ts";
import type { ProviderSignIn } from "../ProviderSignIn.ts";

export type ConnectionDraft = CustomProviderSettings;

export interface AddDraft extends ConnectionDraft {
  readonly route: ProviderRoute;
}

export type ProviderSettingsOperation =
  | "add"
  | "save"
  | "changeBinding"
  | "remove"
  | "disconnect"
  | "replaceApiKey"
  | "beginSignIn"
  | "cancelSignIn"
  | "refreshModels";

export type ProviderSettingsNotice =
  | {
      readonly operation: ProviderSettingsOperation;
      readonly outcome: "succeeded";
      readonly disconnect: ProviderDisconnectResult | null;
    }
  | {
      readonly operation: ProviderSettingsOperation;
      readonly outcome: "rejected";
      readonly httpStatus?: number;
    }
  | {
      readonly operation: ProviderSettingsOperation;
      readonly outcome: "uncertain";
      readonly httpStatus?: number;
    };

export type ProviderCredentialState =
  | "unknown"
  | "disconnecting"
  | "noAuthentication"
  | "noCredentials"
  | "signInRequired"
  | "planAccessMissing"
  | "connected";

export interface Confirmation {
  readonly kind: "binding" | "disconnect" | "remove";
  readonly connection: ProviderConnection;
  readonly settings?: CustomProviderSettings;
}

export interface ProviderSettingsSnapshot {
  readonly connections: readonly ProviderConnection[];
  readonly selectedId: string | null;
  readonly loaded: boolean;
  readonly reading: boolean;
  readonly pending: ProviderSettingsOperation | null;
  readonly notice: ProviderSettingsNotice | null;
  readonly uncertain: boolean;
  readonly reviewed: boolean;
  readonly errors: Readonly<Record<string, string>>;
  readonly credentials: Readonly<Record<string, ProviderCredentialStatus>>;
  readonly catalogs: Readonly<Record<string, ProviderModelCatalog>>;
  readonly signIns: Readonly<Record<string, ProviderSignIn>>;
  readonly drafts: Readonly<Record<string, ConnectionDraft>>;
  readonly add: AddDraft;
  readonly apiKey: string;
  readonly confirmation: Confirmation | null;
}

export function providerHostname(endpoint: string): string {
  try {
    return new URL(endpoint).hostname;
  } catch {
    return "";
  }
}

export function signInPending(signIn: ProviderSignIn): boolean {
  return signIn.status === "pending" || signIn.status === "completing";
}

export function credentialState(
  connection: ProviderConnection,
  status: ProviderCredentialStatus | undefined,
): ProviderCredentialState {
  if (!status) return "unknown";
  if (status.isDisconnecting) return "disconnecting";
  if (connection.authentication === "none") return "noAuthentication";
  if (!status.hasCredentials) return "noCredentials";
  if (status.requiresSignIn) return "signInRequired";
  if (status.planAccessGranted === false) return "planAccessMissing";
  return "connected";
}

// Setup is credential presence, not current validity, plan access or catalog discovery.
export function providerSetupState(
  connection: ProviderConnection,
  status: ProviderCredentialStatus | undefined,
): "complete" | "incomplete" | "unknown" {
  if (connection.authentication === "none") return "complete";
  if (!status) return "unknown";
  return status.hasCredentials ? "complete" : "incomplete";
}

export function matchesConnection(connection: ProviderConnection, search: string): boolean {
  const query = search.trim().toLocaleLowerCase();
  return [connection.name, connection.route, connection.endpoint].some((value) =>
    value.toLocaleLowerCase().includes(query),
  );
}

function draftOf(connection: ProviderConnection): ConnectionDraft {
  return {
    name: connection.name,
    endpoint: connection.endpoint,
    api: connection.api,
    authentication: connection.authentication === "none" ? "none" : "apiKey",
  };
}

function readFailure(error: unknown): string {
  return error instanceof RequestError
    ? `Read failed (HTTP ${error.status}). Previous information is retained.`
    : "Read failed. Previous information is retained.";
}

// Kept alive by the always-mounted hook. Commands never receive an AbortSignal.
export class ProviderSettingsController {
  private currentSnapshot: ProviderSettingsSnapshot = {
    connections: [],
    selectedId: null,
    loaded: false,
    reading: false,
    pending: null,
    notice: null,
    uncertain: false,
    reviewed: false,
    errors: {},
    credentials: {},
    catalogs: {},
    signIns: {},
    drafts: {},
    add: {
      route: "chatGpt",
      name: "",
      endpoint: "",
      api: "openAIResponses",
      authentication: "apiKey",
    },
    apiKey: "",
    confirmation: null,
  };

  private active = false;
  private visible = false;
  private readAbort: AbortController | null = null;
  private timer: ReturnType<typeof setTimeout> | undefined;
  private readonly listeners = new Set<() => void>();
  private readonly client: ProviderClient;
  private readonly changed: () => void;

  constructor(client: ProviderClient, changed: () => void) {
    this.client = client;
    this.changed = changed;
  }

  get snapshot(): ProviderSettingsSnapshot {
    return this.currentSnapshot;
  }

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  setActivity(active: boolean, visible: boolean): void {
    const wasReadable = this.canRead;
    this.active = active;
    this.visible = visible;
    if (!active) this.patch({ apiKey: "", confirmation: null });
    if (!this.canRead) this.stopReads();
    else if (!wasReadable) void this.read();
  }

  private get canRead(): boolean {
    return this.active && this.visible && this.snapshot.pending === null;
  }

  private get canMutate(): boolean {
    return this.active && this.snapshot.pending === null && !this.snapshot.uncertain;
  }

  private patch(update: Partial<ProviderSettingsSnapshot>): void {
    this.currentSnapshot = { ...this.currentSnapshot, ...update };
    for (const listener of this.listeners) listener();
  }

  private stopReads(): void {
    clearTimeout(this.timer);
    this.timer = undefined;
    this.readAbort?.abort();
    this.readAbort = null;
    if (this.snapshot.reading) this.patch({ reading: false });
  }

  select(id: string | null): void {
    this.patch({ selectedId: id, apiKey: "", confirmation: null });
    if (this.canRead) {
      this.stopReads();
      void this.read();
    }
  }

  editAdd(update: Partial<AddDraft>): void {
    this.patch({ add: { ...this.snapshot.add, ...update } });
  }

  edit(id: string, update: Partial<ConnectionDraft>): void {
    const draft = this.snapshot.drafts[id];
    if (!draft || this.snapshot.confirmation) return;
    this.patch({ drafts: { ...this.snapshot.drafts, [id]: { ...draft, ...update } } });
  }

  setApiKey(apiKey: string): void {
    this.patch({ apiKey });
  }

  dismissConfirmation(): void {
    this.patch({ confirmation: null });
  }

  allowNewCommands(): void {
    if (this.snapshot.uncertain && this.snapshot.reviewed)
      this.patch({ uncertain: false, reviewed: false, notice: null });
  }

  async read(): Promise<void> {
    if (!this.canRead || this.readAbort) return;
    this.stopReads();
    const abort = new AbortController();
    this.readAbort = abort;
    this.patch({ reading: true });
    const current = () => this.readAbort === abort && !abort.signal.aborted;
    const observe = async <T>(
      key: string,
      request: () => Promise<T>,
      apply: (value: T) => void,
    ) => {
      try {
        const value = await request();
        if (!current()) return;
        apply(value);
        this.clearError(key);
      } catch (error) {
        if (current()) this.setError(key, readFailure(error));
      }
    };
    try {
      await observe(
        "connections",
        () => this.client.list(abort.signal),
        (connections) => {
          const drafts = Object.fromEntries(
            connections.map((connection) => [connection.id, this.savedDraft(connection)]),
          );
          const ids = new Set(connections.map((connection) => connection.id));
          let selectedId = this.snapshot.selectedId;
          if (!this.snapshot.loaded || (selectedId && !ids.has(selectedId)))
            selectedId = connections[0]?.id ?? null;
          this.patch({
            connections,
            drafts,
            selectedId,
            loaded: true,
            reviewed: this.snapshot.uncertain,
            credentials: Object.fromEntries(
              Object.entries(this.snapshot.credentials).filter(([id]) => ids.has(id)),
            ),
            catalogs: Object.fromEntries(
              Object.entries(this.snapshot.catalogs).filter(([id]) => ids.has(id)),
            ),
            errors: Object.fromEntries(
              Object.entries(this.snapshot.errors).filter(
                ([key]) => key === "connections" || ids.has(key.slice(key.indexOf(":") + 1)),
              ),
            ),
            signIns: Object.fromEntries(
              Object.entries(this.snapshot.signIns).filter(([id]) => ids.has(id)),
            ),
            ...(selectedId !== this.snapshot.selectedId ? { apiKey: "", confirmation: null } : {}),
          });
        },
      );
      if (!current()) return;
      const jobs = this.snapshot.connections.map((connection) =>
        observe(
          `credentials:${connection.id}`,
          () => this.client.getCredentials(connection.id, abort.signal),
          (status) => this.storeCredentials(status),
        ),
      );
      const selectedId = this.snapshot.selectedId;
      if (selectedId)
        jobs.push(
          observe(
            `models:${selectedId}`,
            () => this.client.getModels(selectedId, abort.signal),
            (catalog) => this.storeCatalog(catalog),
          ),
        );
      await Promise.all(jobs);
      if (!current()) return;
      await Promise.all(
        Object.values(this.snapshot.signIns)
          .filter(signInPending)
          .map((signIn) => this.observeSignIn(signIn, abort, current)),
      );
    } finally {
      if (current()) {
        this.readAbort = null;
        this.patch({ reading: false });
        this.schedulePoll();
      }
    }
  }

  private savedDraft(connection: ProviderConnection): ConnectionDraft {
    const draft = this.snapshot.drafts[connection.id];
    const previous = this.snapshot.connections.find((item) => item.id === connection.id);
    if (!draft || !previous) return draftOf(connection);
    const saved = draftOf(previous);
    const edited = Object.keys(saved).some(
      (key) => draft[key as keyof ConnectionDraft] !== saved[key as keyof ConnectionDraft],
    );
    return edited ? draft : draftOf(connection);
  }

  private clearError(key: string): void {
    if (!(key in this.snapshot.errors)) return;
    const errors = { ...this.snapshot.errors };
    delete errors[key];
    this.patch({ errors });
  }

  private setError(key: string, message: string): void {
    this.patch({ errors: { ...this.snapshot.errors, [key]: message } });
  }

  private storeCredentials(status: ProviderCredentialStatus): void {
    this.patch({ credentials: { ...this.snapshot.credentials, [status.connectionId]: status } });
  }

  private storeCatalog(catalog: ProviderModelCatalog): void {
    this.patch({ catalogs: { ...this.snapshot.catalogs, [catalog.connectionId]: catalog } });
  }

  private storeSignIn(signIn: ProviderSignIn): void {
    this.patch({ signIns: { ...this.snapshot.signIns, [signIn.connectionId]: signIn } });
  }

  private async observeSignIn(
    previous: ProviderSignIn,
    abort: AbortController,
    current: () => boolean,
  ): Promise<void> {
    const key = `signIn:${previous.connectionId}`;
    let next: ProviderSignIn;
    try {
      next = await this.client.getSignIn(previous.id, abort.signal);
      if (!current()) return;
      this.storeSignIn(next);
      this.clearError(key);
    } catch (error) {
      if (!current()) return;
      if (error instanceof RequestError && error.status === 404) {
        const signIns = { ...this.snapshot.signIns };
        delete signIns[previous.connectionId];
        this.patch({ signIns });
        this.setError(key, "This sign-in is no longer available. Start sign-in again.");
      } else this.setError(key, readFailure(error));
      return;
    }
    if (signInPending(next)) return;
    if (signInPending(previous)) this.changed();
    try {
      const status = await this.client.getCredentials(next.connectionId, abort.signal);
      if (!current()) return;
      this.storeCredentials(status);
      this.clearError(`credentials:${next.connectionId}`);
    } catch (error) {
      if (current()) this.setError(`credentials:${next.connectionId}`, readFailure(error));
    }
  }

  private schedulePoll(): void {
    clearTimeout(this.timer);
    this.timer = undefined;
    if (!this.canRead || this.readAbort) return;
    const pending = Object.values(this.snapshot.signIns).some(signInPending);
    const delay = pending && Object.keys(this.snapshot.errors).length === 0 ? 2000 : 10000;
    this.timer = setTimeout(() => {
      this.timer = undefined;
      void this.read();
    }, delay);
  }

  private async command<T>(
    operation: ProviderSettingsOperation,
    run: () => Promise<T>,
    apply: (value: T) => ProviderDisconnectResult | void,
  ): Promise<void> {
    if (!this.canMutate) return;
    this.stopReads();
    this.patch({ pending: operation, notice: null, apiKey: "", confirmation: null });
    let changed = false;
    try {
      const result = await run();
      const disconnect = apply(result);
      this.patch({
        notice: {
          operation,
          outcome: "succeeded",
          disconnect: operation === "disconnect" ? (disconnect ?? null) : null,
        },
      });
      changed = true;
    } catch (error) {
      const rejected =
        error instanceof RequestError && [400, 404, 409, 415, 422].includes(error.status);
      this.patch(
        rejected
          ? {
              notice: {
                operation,
                outcome: "rejected",
                httpStatus: error.status,
              },
            }
          : {
              notice: {
                operation,
                outcome: "uncertain",
                ...(error instanceof RequestError ? { httpStatus: error.status } : {}),
              },
              uncertain: true,
              reviewed: false,
            },
      );
      changed = !rejected;
    } finally {
      this.patch({ pending: null });
    }
    if (changed) this.changed();
    if (changed && this.canRead) await this.read();
    else this.schedulePoll();
  }

  private storeConnection(connection: ProviderConnection): void {
    const exists = this.snapshot.connections.some((item) => item.id === connection.id);
    this.patch({
      connections: exists
        ? this.snapshot.connections.map((item) => (item.id === connection.id ? connection : item))
        : [...this.snapshot.connections, connection],
      drafts: { ...this.snapshot.drafts, [connection.id]: draftOf(connection) },
    });
  }

  async create(): Promise<void> {
    const { route, ...draft } = this.snapshot.add;
    const settings = {
      ...draft,
      name: providerHostname(route === "custom" ? draft.endpoint : "https://api.openai.com"),
    };
    await this.command(
      "add",
      () => {
        if (route === "chatGpt") return this.client.createChatGpt(settings.name);
        if (route === "openAI") return this.client.createOpenAI(settings.name);
        return this.client.createCustom(settings);
      },
      (connection) => {
        this.storeConnection(connection);
        this.patch({
          ...(this.snapshot.selectedId === null ? { selectedId: connection.id } : {}),
          add: { ...this.snapshot.add, name: "" },
        });
      },
    );
  }

  async save(connection: ProviderConnection): Promise<void> {
    if (!this.canMutate || connection.route !== "custom") return;
    const draft = this.snapshot.drafts[connection.id];
    if (!draft) return;
    const settings = { ...draft, name: providerHostname(draft.endpoint) };
    const bindingChanged =
      settings.endpoint !== connection.endpoint ||
      settings.api !== connection.api ||
      settings.authentication !== connection.authentication;
    if (bindingChanged) {
      this.patch({ confirmation: { kind: "binding", connection, settings } });
      return;
    }
    await this.command(
      "save",
      () => this.client.updateCustom(connection.id, settings),
      (updated) => this.storeConnection(updated),
    );
  }

  requestConfirmation(kind: "disconnect" | "remove", connection: ProviderConnection): void {
    if (this.canMutate) this.patch({ confirmation: { kind, connection } });
  }

  async confirm(): Promise<void> {
    const confirmation = this.snapshot.confirmation;
    if (!confirmation || !this.canMutate) return;
    const { connection, kind, settings } = confirmation;
    if (kind === "binding" && settings) {
      await this.command(
        "changeBinding",
        () => this.client.updateCustom(connection.id, settings),
        (updated) => {
          this.storeConnection(updated);
          this.forgetStatus(connection.id);
        },
      );
    } else if (kind === "remove") {
      await this.command(
        "remove",
        () => this.client.remove(connection.id),
        () => {
          const connections = this.snapshot.connections.filter((item) => item.id !== connection.id);
          this.forgetStatus(connection.id);
          const drafts = { ...this.snapshot.drafts };
          delete drafts[connection.id];
          this.patch({
            connections,
            drafts,
            selectedId:
              this.snapshot.selectedId === connection.id
                ? (connections[0]?.id ?? null)
                : this.snapshot.selectedId,
          });
        },
      );
    } else if (kind === "disconnect") {
      await this.command(
        "disconnect",
        () => this.client.disconnect(connection.id),
        (result) => {
          this.forgetStatus(connection.id);
          return result;
        },
      );
    }
  }

  private forgetStatus(id: string): void {
    const credentials = { ...this.snapshot.credentials };
    const catalogs = { ...this.snapshot.catalogs };
    const signIns = { ...this.snapshot.signIns };
    delete credentials[id];
    delete catalogs[id];
    delete signIns[id];
    this.patch({ credentials, catalogs, signIns });
  }

  async replaceApiKey(connection: ProviderConnection): Promise<void> {
    const key = this.snapshot.apiKey;
    if (!key) return;
    await this.command(
      "replaceApiKey",
      () => this.client.setApiKey(connection, key),
      (status) => this.storeCredentials(status),
    );
  }

  async beginSignIn(connection: ProviderConnection): Promise<void> {
    if (
      Object.values(this.snapshot.signIns).some(
        (item) => item.connectionId === connection.id && signInPending(item),
      )
    )
      return;
    await this.command(
      "beginSignIn",
      () => this.client.beginSignIn(connection.id),
      (signIn) => this.storeSignIn(signIn),
    );
  }

  async cancelSignIn(signIn: ProviderSignIn): Promise<void> {
    await this.command(
      "cancelSignIn",
      () => this.client.cancelSignIn(signIn.id),
      (updated) => this.storeSignIn(updated),
    );
  }

  async refreshModels(connection: ProviderConnection): Promise<void> {
    await this.command(
      "refreshModels",
      () => this.client.refreshModels(connection.id),
      (catalog) => this.storeCatalog(catalog),
    );
  }
}
