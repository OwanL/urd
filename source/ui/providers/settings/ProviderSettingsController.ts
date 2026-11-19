import { RequestError } from "../../http/RequestError.ts";
import type { ProviderConnection } from "../ProviderConnection.ts";
import type { ProviderCredentialStatus, ProviderDisconnectResult } from "../ProviderCredentials.ts";
import type { ProviderModelCatalog } from "../ProviderModels.ts";
import type { ModelSettings } from "../ModelSettings.ts";
import type { ProviderSignIn } from "../ProviderSignIn.ts";
import { credentialState, providerHostname, signInPending } from "./ProviderConnectionStatus.ts";
import type {
  AddDraft,
  ConnectionDraft,
  ProviderSettingsClient,
  ProviderSettingsOperation,
  ProviderSettingsSnapshot,
} from "./ProviderSettingsTypes.ts";

interface SetupContext {
  readonly connection: ProviderConnection;
  readonly view: number;
}

interface BucketSetupRequest extends SetupContext {
  readonly abort: AbortController;
}

function sameConnection(first: ProviderConnection, second: ProviderConnection): boolean {
  return (
    first.id === second.id &&
    first.name === second.name &&
    first.route === second.route &&
    first.endpoint === second.endpoint &&
    first.api === second.api &&
    first.authentication === second.authentication
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
    connectionsReloadedAfterUncertainty: false,
    errors: {},
    credentials: {},
    catalogs: {},
    modelSettings: {},
    modelDrafts: {},
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
    bucketSetup: null,
  };

  private setupVisible = false;
  private setupView = 0;
  private bucketRequest: BucketSetupRequest | null = null;
  private readonly setupSignIns = new Map<string, SetupContext>();
  private uncertainModelConnectionId: string | null = null;
  private active = false;
  private visible = false;
  private readAbort: AbortController | null = null;
  private timer: ReturnType<typeof setTimeout> | undefined;
  private readonly listeners = new Set<() => void>();
  private readonly client: ProviderSettingsClient;
  private readonly changed: () => void;
  private readonly onReady: ((connectionId: string) => Promise<boolean> | undefined) | undefined;
  private readonly refreshPreferences: (() => Promise<void>) | undefined;

  constructor(
    client: ProviderSettingsClient,
    changed: () => void,
    onReady?: (connectionId: string) => Promise<boolean> | undefined,
    refreshPreferences?: () => Promise<void>,
  ) {
    this.client = client;
    this.changed = changed;
    this.onReady = onReady;
    this.refreshPreferences = refreshPreferences;
  }

  get snapshot(): ProviderSettingsSnapshot {
    return this.currentSnapshot;
  }

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  setActivity(active: boolean, visible: boolean, providersTab = active): void {
    const wasReadable = this.canRead;
    const wasSetupVisible = this.setupVisible;
    this.active = active;
    this.visible = visible;
    this.setupVisible = active && providersTab;
    if (wasSetupVisible && !this.setupVisible) this.leaveSetupView();
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
    if (this.bucketRequest && !this.isCurrentSetup(this.bucketRequest)) {
      this.clearBucketRequest();
      this.currentSnapshot = { ...this.currentSnapshot, bucketSetup: null };
    }
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
    this.leaveSetupView();
    this.patch({ selectedId: id, apiKey: "", confirmation: null });
    if (this.canRead) {
      this.stopReads();
      void this.read();
    }
  }

  private leaveSetupView(): void {
    this.setupView++;
    this.skipBucketSuggestions();
  }

  private isCurrentSetup({ connection, view }: SetupContext): boolean {
    if (!this.setupVisible || view !== this.setupView || this.snapshot.selectedId !== connection.id)
      return false;
    const saved = this.snapshot.connections.find(({ id }) => id === connection.id);
    if (!saved || !sameConnection(saved, connection)) return false;
    const status = this.snapshot.credentials[connection.id];
    if (status?.isDisconnecting) return false;
    return (
      connection.authentication === "none" || credentialState(connection, status) === "connected"
    );
  }

  private clearBucketRequest(): void {
    this.bucketRequest?.abort.abort();
    this.bucketRequest = null;
  }

  skipBucketSuggestions(): void {
    this.clearBucketRequest();
    if (this.snapshot.bucketSetup) this.patch({ bucketSetup: null });
  }

  private notifyProviderReady(context: SetupContext): boolean {
    const ready = this.onReady?.(context.connection.id);
    if (!ready) return false;
    void this.offerBucketsAfterRefresh(context, ready);
    return true;
  }

  private async offerBucketsAfterRefresh(
    context: SetupContext,
    ready: Promise<boolean>,
  ): Promise<void> {
    const request = { ...context, abort: new AbortController() };
    if (this.isCurrentSetup(context)) {
      this.clearBucketRequest();
      this.bucketRequest = request;
      this.patch({ bucketSetup: { phase: "loading" } });
    }
    const current = () => this.bucketRequest === request;
    try {
      const refreshed = await ready;
      if (!current()) return;
      if (!refreshed) {
        this.skipBucketSuggestions();
        return;
      }
      const proposal = await this.client.getBucketSuggestions(
        context.connection.id,
        request.abort.signal,
      );
      if (!current()) return;
      if (sameConnection(proposal.connection, context.connection) && proposal.assignments.length)
        this.patch({ bucketSetup: { phase: "offered", proposal } });
      else this.skipBucketSuggestions();
    } catch {
      if (current())
        this.patch({
          bucketSetup: {
            phase: "finished",
            message: "Could not load bucket suggestions.",
            error: true,
          },
        });
    }
  }

  async confirmBucketSuggestions(): Promise<void> {
    const request = this.bucketRequest;
    const setup = this.snapshot.bucketSetup;
    if (!request || setup?.phase !== "offered" || !this.canMutate || this.snapshot.confirmation)
      return;
    this.patch({ bucketSetup: { phase: "saving", proposal: setup.proposal } });
    try {
      await this.client.confirmBucketSuggestions(setup.proposal);
    } catch (error) {
      if (this.bucketRequest === request)
        this.patch({
          bucketSetup: {
            phase: "finished",
            message:
              error instanceof RequestError && error.status === 409
                ? "Suggestions changed. Review your assignments in Model buckets."
                : "Could not confirm the save. Check your assignments in Model buckets.",
            error: true,
          },
        });
      return;
    }
    await this.refreshPreferences?.();
    if (this.bucketRequest === request)
      this.patch({ bucketSetup: { phase: "finished", message: "Model buckets saved." } });
  }

  editAdd(update: Partial<AddDraft>): void {
    this.patch({ add: { ...this.snapshot.add, ...update } });
  }

  edit(id: string, update: Partial<ConnectionDraft>): void {
    const draft = this.snapshot.drafts[id];
    if (!draft || this.snapshot.confirmation) return;
    this.patch({ drafts: { ...this.snapshot.drafts, [id]: { ...draft, ...update } } });
  }

  editModel(
    settings: ModelSettings,
    update: Partial<Pick<ModelSettings, "family" | "supportsImages" | "contextWindow">>,
  ): void {
    if (!this.canMutate || !this.snapshot.modelSettings[settings.connectionId]) return;
    const drafts = this.snapshot.modelDrafts[settings.connectionId] ?? [];
    const draft = drafts.find((item) => item.modelId === settings.modelId) ?? settings;
    this.patch({
      modelDrafts: {
        ...this.snapshot.modelDrafts,
        [settings.connectionId]: [
          ...drafts.filter((item) => item.modelId !== settings.modelId),
          { ...draft, ...update },
        ],
      },
    });
  }

  setApiKey(apiKey: string): void {
    this.patch({ apiKey });
  }

  dismissConfirmation(): void {
    this.patch({ confirmation: null });
  }

  allowNewCommands(): void {
    if (this.snapshot.uncertain && this.snapshot.connectionsReloadedAfterUncertainty) {
      this.uncertainModelConnectionId = null;
      this.patch({ uncertain: false, connectionsReloadedAfterUncertainty: false, notice: null });
    }
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
            connectionsReloadedAfterUncertainty:
              this.snapshot.uncertain &&
              (this.uncertainModelConnectionId === null ||
                !ids.has(this.uncertainModelConnectionId)),
            credentials: Object.fromEntries(
              Object.entries(this.snapshot.credentials).filter(([id]) => ids.has(id)),
            ),
            catalogs: Object.fromEntries(
              Object.entries(this.snapshot.catalogs).filter(([id]) => ids.has(id)),
            ),
            modelSettings: Object.fromEntries(
              Object.entries(this.snapshot.modelSettings).filter(([id]) => ids.has(id)),
            ),
            modelDrafts: Object.fromEntries(
              Object.entries(this.snapshot.modelDrafts).filter(([id]) => ids.has(id)),
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
      const jobs = this.snapshot.connections.flatMap((connection) => [
        observe(
          `credentials:${connection.id}`,
          () => this.client.getCredentials(connection.id, abort.signal),
          (status) => this.storeCredentials(status),
        ),
        observe(
          `modelSettings:${connection.id}`,
          () => this.client.getModelSettings(connection.id, abort.signal),
          (settings) => this.storeModelSettings(connection.id, settings),
        ),
      ]);
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
      const uncertainId = this.uncertainModelConnectionId;
      if (
        this.snapshot.uncertain &&
        uncertainId &&
        !this.snapshot.errors.connections &&
        !this.snapshot.errors[`modelSettings:${uncertainId}`] &&
        this.snapshot.modelSettings[uncertainId]
      )
        this.patch({ connectionsReloadedAfterUncertainty: true });
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

  private storeModelSettings(connectionId: string, settings: readonly ModelSettings[]): void {
    this.patch({ modelSettings: { ...this.snapshot.modelSettings, [connectionId]: settings } });
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
      if (signInPending(next)) this.storeSignIn(next);
      this.clearError(key);
    } catch (error) {
      if (!current()) return;
      if (error instanceof RequestError && error.status === 404) {
        const signIns = { ...this.snapshot.signIns };
        delete signIns[previous.connectionId];
        this.setupSignIns.delete(previous.id);
        this.patch({ signIns });
        this.setError(key, "This sign-in is no longer available. Start sign-in again.");
      } else this.setError(key, readFailure(error));
      return;
    }
    if (signInPending(next)) return;
    const context = this.setupSignIns.get(previous.id);
    let credentials: ProviderCredentialStatus | undefined;
    try {
      credentials = await this.client.getCredentials(next.connectionId, abort.signal);
      if (!current()) return;
      this.storeCredentials(credentials);
      this.clearError(`credentials:${next.connectionId}`);
    } catch (error) {
      if (!current()) return;
      this.setError(`credentials:${next.connectionId}`, readFailure(error));
    }
    this.storeSignIn(next);
    this.setupSignIns.delete(previous.id);
    if (
      context &&
      next.status === "completed" &&
      credentials &&
      credentialState(context.connection, credentials) === "connected" &&
      this.notifyProviderReady(context)
    )
      return;
    this.changed();
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
    options?: {
      modelConnectionId?: string;
      afterSuccess?: (value: T) => boolean;
    },
  ): Promise<void> {
    if (!this.canMutate) return;
    this.stopReads();
    this.patch({ pending: operation, notice: null, apiKey: "", confirmation: null });
    let changed = false;
    let afterSuccess: (() => boolean) | undefined;
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
      afterSuccess = () => options?.afterSuccess?.(result) ?? false;
    } catch (error) {
      const rejected =
        error instanceof RequestError && [400, 404, 409, 415, 422].includes(error.status);
      if (!rejected) this.uncertainModelConnectionId = options?.modelConnectionId ?? null;
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
              connectionsReloadedAfterUncertainty: false,
            },
      );
      changed = !rejected;
    } finally {
      this.patch({ pending: null });
    }
    if (changed && !afterSuccess?.()) this.changed();
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
    const view = this.setupView;
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
      {
        afterSuccess: (connection) =>
          connection.authentication === "none" && this.notifyProviderReady({ connection, view }),
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
    if (this.bucketRequest?.connection.id === connection.id) this.skipBucketSuggestions();
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
          const modelSettings = { ...this.snapshot.modelSettings };
          const modelDrafts = { ...this.snapshot.modelDrafts };
          delete drafts[connection.id];
          delete modelSettings[connection.id];
          delete modelDrafts[connection.id];
          this.patch({
            connections,
            drafts,
            modelSettings,
            modelDrafts,
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
    if (this.bucketRequest?.connection.id === id) this.skipBucketSuggestions();
    const signIn = this.snapshot.signIns[id];
    if (signIn) this.setupSignIns.delete(signIn.id);
    const credentials = { ...this.snapshot.credentials };
    const catalogs = { ...this.snapshot.catalogs };
    const signIns = { ...this.snapshot.signIns };
    delete credentials[id];
    delete catalogs[id];
    delete signIns[id];
    this.patch({ credentials, catalogs, signIns });
  }

  async replaceApiKey(connection: ProviderConnection, setup = false): Promise<void> {
    const view = this.setupView;
    const initialKey = setup && !this.snapshot.credentials[connection.id]?.hasCredentials;
    const key = this.snapshot.apiKey;
    if (!key) return;
    await this.command(
      "replaceApiKey",
      () => this.client.setApiKey(connection, key),
      (status) => this.storeCredentials(status),
      {
        afterSuccess: (status) =>
          initialKey &&
          credentialState(connection, status) === "connected" &&
          this.notifyProviderReady({ connection, view }),
      },
    );
  }

  async beginSignIn(connection: ProviderConnection, setup = false): Promise<void> {
    const view = this.setupView;
    const initialSignIn = setup && !this.snapshot.credentials[connection.id]?.hasCredentials;
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
      {
        afterSuccess: (signIn) => {
          if (!initialSignIn || !signInPending(signIn)) return false;
          this.setupSignIns.set(signIn.id, { connection, view });
          return true; // Discovery waits for successful authentication.
        },
      },
    );
  }

  async cancelSignIn(signIn: ProviderSignIn): Promise<void> {
    await this.command(
      "cancelSignIn",
      () => this.client.cancelSignIn(signIn.id),
      (updated) => {
        this.setupSignIns.delete(signIn.id);
        this.storeSignIn(updated);
      },
    );
  }

  async saveModelSettings(settings: ModelSettings): Promise<void> {
    if (!this.snapshot.modelSettings[settings.connectionId]) return;
    await this.command(
      "saveModelSettings",
      () => this.client.saveModelSettings(settings),
      (saved) => {
        const records = this.snapshot.modelSettings[saved.connectionId] ?? [];
        this.storeModelSettings(saved.connectionId, [
          ...records.filter((item) => item.modelId !== saved.modelId),
          saved,
        ]);
        this.patch({
          modelDrafts: {
            ...this.snapshot.modelDrafts,
            [saved.connectionId]: (this.snapshot.modelDrafts[saved.connectionId] ?? []).filter(
              (item) => item.modelId !== saved.modelId,
            ),
          },
        });
      },
      { modelConnectionId: settings.connectionId },
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
