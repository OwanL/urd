import type { ModelSettings } from "../ModelSettings.ts";
import type { ProviderConnection } from "../ProviderConnection.ts";
import type { ProviderCredentialStatus } from "../ProviderCredentials.ts";
import type { ProviderSignIn } from "../ProviderSignIn.ts";
import {
  canOfferBuckets,
  offerBucketsAfterRefresh,
  saveBucketSuggestions,
} from "./ProviderBucketSetup.ts";
import { connectionCommands } from "./ProviderConnectionCommands.ts";
import { forgetConnectionStatus } from "./ProviderConnectionRecords.ts";
import { credentialState, signInPending } from "./ProviderConnectionStatus.ts";
import { credentialCommands } from "./ProviderCredentialCommands.ts";
import { modelCommands, type ModelSettingsEdit } from "./ProviderModelCommands.ts";
import { commandFailureNotice, type RunProviderCommand } from "./ProviderSettingsCommand.ts";
import { readProviderSettings } from "./ProviderSettingsReadCycle.ts";
import { authorizationLink, reserveSignInTab } from "./ProviderSignInTab.ts";
import type {
  AddDraft,
  ConnectionDraft,
  ProviderSettingsClient,
  ProviderSettingsSnapshot,
} from "./ProviderSettingsTypes.ts";

interface SetupContext {
  readonly connection: ProviderConnection;
  readonly view: number;
}

interface BucketSetupRequest extends SetupContext {
  readonly abort: AbortController;
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
    blockedSignIns: {},
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
  private signInIntent = 0;
  private openingTab = false;
  private readonly reservations = new Set<Window>();
  private newConnectionId: string | null = null;
  private bucketRequest: BucketSetupRequest | null = null;
  private readonly setupSignIns = new Map<string, SetupContext>();
  private uncertainModelConnectionId: string | null = null;
  private disposed = false;
  private active = false;
  private visible = false;
  private readAbort: AbortController | null = null;
  private timer: ReturnType<typeof setTimeout> | undefined;
  private readonly listeners = new Set<() => void>();
  private readonly client: ProviderSettingsClient;
  private readonly changed: () => void;
  private readonly onReady: ((connectionId: string) => Promise<boolean> | undefined) | undefined;
  private readonly refreshPreferences: (() => Promise<void>) | undefined;
  private readonly offerProviderBuckets: boolean;
  private readonly connectionCommands: ReturnType<typeof connectionCommands>;
  private readonly credentialCommands: ReturnType<typeof credentialCommands>;
  private readonly modelCommands: ReturnType<typeof modelCommands>;

  constructor(
    client: ProviderSettingsClient,
    changed: () => void,
    onReady?: (connectionId: string) => Promise<boolean> | undefined,
    refreshPreferences?: () => Promise<void>,
    offerProviderBuckets = true,
  ) {
    this.client = client;
    this.changed = changed;
    this.onReady = onReady;
    this.refreshPreferences = refreshPreferences;
    this.offerProviderBuckets = offerProviderBuckets;
    const snapshot = () => this.snapshot;
    const publish = (update: Partial<ProviderSettingsSnapshot>) => this.patch(update);
    this.connectionCommands = connectionCommands(client, this.command, snapshot, publish, (id) =>
      this.forgetStatus(id),
    );
    this.credentialCommands = credentialCommands(client, this.command, snapshot, publish);
    this.modelCommands = modelCommands(client, this.command, snapshot, publish);
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
    if (this.visible && !visible && !this.openingTab && this.reservations.size === 0)
      this.signInIntent++;
    this.active = active;
    this.visible = visible;
    this.setupVisible = active && providersTab;
    if (wasSetupVisible && !this.setupVisible) this.leaveSetupView();
    if (!active) this.patch({ apiKey: "", confirmation: null });
    if (!this.canRead) this.stopReads();
    if (this.canRead && !wasReadable) void this.read();
  }

  private get canRead(): boolean {
    return !this.disposed && this.active && this.visible && this.snapshot.pending === null;
  }

  dispose(): void {
    this.disposed = true;
    this.stopReads();
    for (const tab of this.reservations) this.closeReservation(tab);
    this.setupSignIns.clear();
    this.clearBucketRequest();
  }

  private get canMutate(): boolean {
    return (
      !this.disposed && this.active && this.snapshot.pending === null && !this.snapshot.uncertain
    );
  }

  private patch(update: Partial<ProviderSettingsSnapshot>): void {
    this.currentSnapshot = { ...this.currentSnapshot, ...update };
    if (this.bucketRequest && !this.isCurrentSetup(this.bucketRequest)) {
      this.clearBucketRequest();
      this.currentSnapshot = { ...this.currentSnapshot, bucketSetup: null };
    }
    for (const listener of this.listeners) listener();
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
    this.newConnectionId = null;
    this.skipBucketSuggestions();
  }

  private isCurrentSetup({ connection, view }: SetupContext): boolean {
    if (!this.setupVisible || view !== this.setupView || this.snapshot.selectedId !== connection.id)
      return false;
    const saved = this.snapshot.connections.find(({ id }) => id === connection.id);
    return canOfferBuckets(connection, saved, this.snapshot.credentials[connection.id]);
  }

  private clearBucketRequest(): void {
    this.bucketRequest?.abort.abort();
    this.bucketRequest = null;
  }

  skipBucketSuggestions(): void {
    this.clearBucketRequest();
    if (this.snapshot.bucketSetup) this.patch({ bucketSetup: null });
  }

  private notifyProviderReady(context: SetupContext, newlyCreated = false): boolean {
    const offer = newlyCreated || this.newConnectionId === context.connection.id;
    if (offer) this.newConnectionId = null;
    const ready = this.onReady?.(context.connection.id);
    if (!ready) return false;
    if (offer && this.offerProviderBuckets) void this.startBucketSuggestions(context, ready);
    else void ready.catch(() => {});
    return true;
  }

  private async startBucketSuggestions(
    context: SetupContext,
    ready: Promise<boolean>,
  ): Promise<void> {
    const request = { ...context, abort: new AbortController() };
    if (this.isCurrentSetup(context)) {
      this.clearBucketRequest();
      this.bucketRequest = request;
      this.patch({ bucketSetup: { phase: "loading" } });
    }
    await offerBucketsAfterRefresh(
      context.connection,
      ready,
      () => this.client.getBucketSuggestions(context.connection.id, request.abort.signal),
      () => this.bucketRequest === request,
      (setup) => (setup ? this.patch({ bucketSetup: setup }) : this.skipBucketSuggestions()),
    );
  }

  async confirmBucketSuggestions(): Promise<void> {
    const request = this.bucketRequest;
    const setup = this.snapshot.bucketSetup;
    if (!request || setup?.phase !== "offered" || !this.canMutate || this.snapshot.confirmation)
      return;
    this.patch({ bucketSetup: { phase: "saving", proposal: setup.proposal } });
    await saveBucketSuggestions(
      () => this.client.confirmBucketSuggestions(setup.proposal),
      () => this.refreshPreferences?.(),
      (setup) => {
        if (this.bucketRequest === request) this.patch({ bucketSetup: setup });
      },
    );
  }

  editAdd(update: Partial<AddDraft>): void {
    this.patch({ add: { ...this.snapshot.add, ...update } });
  }

  edit(id: string, update: Partial<ConnectionDraft>): void {
    const draft = this.snapshot.drafts[id];
    if (!draft || this.snapshot.confirmation) return;
    if (this.snapshot.selectedId === id) this.signInIntent++;
    if (this.newConnectionId === id) this.newConnectionId = null;
    this.patch({ drafts: { ...this.snapshot.drafts, [id]: { ...draft, ...update } } });
  }

  editModel(settings: ModelSettings, update: ModelSettingsEdit): void {
    if (this.canMutate) this.modelCommands.edit(settings, update);
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

  private stopReads(): void {
    clearTimeout(this.timer);
    this.timer = undefined;
    this.readAbort?.abort();
    this.readAbort = null;
    if (this.snapshot.reading) this.patch({ reading: false });
  }

  async read(): Promise<void> {
    if (!this.canRead || this.readAbort) return;
    this.stopReads();
    const abort = new AbortController();
    this.readAbort = abort;
    this.patch({ reading: true });
    const isCurrent = () => this.readAbort === abort && !abort.signal.aborted;
    try {
      await readProviderSettings(this.client, {
        signal: abort.signal,
        isCurrent,
        snapshot: () => this.snapshot,
        publish: (update) => this.patch(update),
        uncertainModelConnectionId: () => this.uncertainModelConnectionId,
        forgetSetupSignIn: (id) => this.setupSignIns.delete(id),
        captureSignInCompletion: (id) => this.captureSignInCompletion(id),
      });
    } finally {
      if (isCurrent()) {
        this.readAbort = null;
        this.patch({ reading: false });
        this.schedulePoll();
      }
    }
  }

  private captureSignInCompletion(id: string) {
    const context = this.setupSignIns.get(id);
    return (next: ProviderSignIn, credentials: ProviderCredentialStatus | undefined) => {
      this.setupSignIns.delete(id);
      if (
        context &&
        next.status === "completed" &&
        credentials &&
        credentialState(context.connection, credentials) === "connected" &&
        this.notifyProviderReady(context)
      )
        return;
      this.changed();
    };
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

  private readonly command: RunProviderCommand = async (operation, run, apply, options) => {
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
      const notice = commandFailureNotice(operation, error);
      if (notice.outcome === "rejected") this.patch({ notice });
      else {
        this.uncertainModelConnectionId = options?.modelConnectionId ?? null;
        this.patch({ notice, uncertain: true, connectionsReloadedAfterUncertainty: false });
        changed = true;
      }
    } finally {
      this.patch({ pending: null });
    }
    if (changed && !afterSuccess?.()) this.changed();
    if (changed && this.canRead) await this.read();
    else this.schedulePoll();
  };

  create(): Promise<void> {
    return this.createConnection();
  }

  async createAndSignIn(): Promise<void> {
    if (
      !this.canMutate ||
      !this.setupVisible ||
      !this.visible ||
      this.snapshot.selectedId !== null ||
      this.snapshot.add.route !== "chatGpt"
    )
      return;
    const tab = this.reserveTab();
    const existingIds = new Set(this.snapshot.connections.map(({ id }) => id));
    const intent = this.signInIntent;
    let signIn: Promise<void> | undefined;
    await this.createConnection((connection, view) => {
      const saved = this.snapshot.connections.find(({ id }) => id === connection.id);
      if (
        existingIds.has(connection.id) ||
        connection.route !== "chatGpt" ||
        connection.authentication !== "oAuth" ||
        !this.canMutate ||
        !this.setupVisible ||
        (!this.visible && !tab) ||
        this.setupView !== view ||
        this.signInIntent !== intent ||
        this.snapshot.selectedId !== connection.id ||
        !saved ||
        saved.name !== connection.name ||
        saved.route !== connection.route ||
        saved.api !== connection.api ||
        saved.endpoint !== connection.endpoint ||
        saved.authentication !== connection.authentication
      )
        return;
      signIn = this.startSignIn(connection, true, tab);
    });
    if (!signIn) this.closeReservation(tab);
    await signIn;
  }

  private createConnection(
    created?: (connection: ProviderConnection, view: number) => void,
  ): Promise<void> {
    const view = this.setupView;
    return this.connectionCommands.create((connection) => {
      if (connection.authentication === "none")
        return this.notifyProviderReady({ connection, view }, true);
      if (
        this.offerProviderBuckets &&
        this.setupVisible &&
        this.setupView === view &&
        this.snapshot.selectedId === connection.id
      )
        this.newConnectionId = connection.id;
      created?.(connection, view);
      return false;
    });
  }

  save(connection: ProviderConnection): Promise<void> {
    return this.canMutate ? this.connectionCommands.save(connection) : Promise.resolve();
  }

  requestConfirmation(kind: "disconnect" | "remove", connection: ProviderConnection): void {
    if (this.canMutate) this.patch({ confirmation: { kind, connection } });
  }

  async confirm(): Promise<void> {
    const confirmation = this.snapshot.confirmation;
    if (!confirmation || !this.canMutate) return;
    if (this.bucketRequest?.connection.id === confirmation.connection.id)
      this.skipBucketSuggestions();
    await this.connectionCommands.confirm(confirmation);
  }

  private forgetStatus(id: string): void {
    if (this.newConnectionId === id) this.newConnectionId = null;
    if (this.bucketRequest?.connection.id === id) this.skipBucketSuggestions();
    const signIn = this.snapshot.signIns[id];
    if (signIn) {
      this.setupSignIns.delete(signIn.id);
    }
    this.patch(forgetConnectionStatus(this.snapshot, id));
  }

  replaceApiKey(connection: ProviderConnection, setup = false): Promise<void> {
    const view = this.setupView;
    return this.credentialCommands.replaceApiKey(
      connection,
      setup ? () => this.notifyProviderReady({ connection, view }) : undefined,
    );
  }

  beginSignIn(connection: ProviderConnection, setup = false): Promise<void> {
    if (
      !this.canMutate ||
      Object.values(this.snapshot.signIns).some(
        (item) => item.connectionId === connection.id && signInPending(item),
      )
    )
      return Promise.resolve();
    return this.startSignIn(connection, setup, this.reserveTab());
  }

  private async startSignIn(
    connection: ProviderConnection,
    setup: boolean,
    tab: Window | null,
  ): Promise<void> {
    const view = this.setupView;
    let started = false;
    try {
      await this.credentialCommands.beginSignIn(
        connection,
        setup
          ? (id) => {
              if (!this.disposed) this.setupSignIns.set(id, { connection, view });
            }
          : undefined,
        (signIn) => {
          started = true;
          if (this.disposed) {
            this.closeReservation(tab);
            return;
          }
          const url = authorizationLink(signIn.authorizationUrl);
          let blocked = !tab;
          if (tab && signInPending(signIn) && url && this.reservations.has(tab)) {
            try {
              tab.location.href = url;
              this.reservations.delete(tab);
            } catch {
              blocked = true;
              this.closeReservation(tab);
            }
          } else this.closeReservation(tab);
          this.patch({
            blockedSignIns: { ...this.snapshot.blockedSignIns, [connection.id]: blocked },
          });
        },
      );
    } finally {
      if (!started) this.closeReservation(tab);
    }
  }

  private reserveTab(): Window | null {
    this.openingTab = true;
    try {
      const tab = reserveSignInTab();
      if (tab) this.reservations.add(tab);
      return tab;
    } finally {
      this.openingTab = false;
    }
  }

  private closeReservation(tab: Window | null): void {
    if (tab && this.reservations.delete(tab)) tab.close();
  }

  cancelSignIn(signIn: ProviderSignIn): Promise<void> {
    if (this.newConnectionId === signIn.connectionId) this.newConnectionId = null;
    return this.credentialCommands.cancelSignIn(signIn, (id) => {
      this.setupSignIns.delete(id);
    });
  }

  saveModelSettings(settings: ModelSettings): Promise<void> {
    return this.modelCommands.save(settings);
  }

  refreshModels(connection: ProviderConnection): Promise<void> {
    return this.modelCommands.refresh(connection);
  }
}
