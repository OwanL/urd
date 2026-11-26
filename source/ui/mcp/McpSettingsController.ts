import { RequestError } from "../http/RequestError.ts";
import {
  connectRejection,
  connectRequest,
  type McpConnectForm,
  type McpConnectProblem,
} from "./McpConnectRequest.ts";
import type { McpSettingsClient } from "./McpClient.ts";
import type { McpOperation, McpServer } from "./McpServer.ts";

export type McpScreen = "list" | "add" | "detail";

export interface McpSettingsSnapshot {
  servers: readonly McpServer[] | null;
  loadError: string;
  screen: McpScreen;
  selectedId: string | null;
  form: McpConnectForm;
  busy: boolean;
  operationId: string | null;
  operation: McpOperation | null;
  error: string;
}

// Owns MCP settings state, commands and connection tracking; the hook keeps it alive
// across closing the settings popover, and views render only from the snapshot.
export class McpSettingsController {
  private currentSnapshot: McpSettingsSnapshot = {
    servers: null,
    loadError: "",
    screen: "list",
    selectedId: null,
    form: emptyForm(),
    busy: false,
    operationId: null,
    operation: null,
    error: "",
  };

  private active = false;
  private disposed = false;
  private readToken = 0;
  private pollToken = 0;
  private timer: ReturnType<typeof setTimeout> | undefined;
  private readonly listeners = new Set<() => void>();
  private readonly client: McpSettingsClient;

  constructor(client: McpSettingsClient) {
    this.client = client;
  }

  get snapshot(): McpSettingsSnapshot {
    return this.currentSnapshot;
  }

  subscribe(listener: () => void): () => void {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  }

  setActivity(active: boolean): void {
    const wasActive = this.active;
    this.active = active;
    if (active && !wasActive && !this.disposed) void this.read();
  }

  dispose(): void {
    this.disposed = true;
    this.forgetOperation();
  }

  async read(): Promise<void> {
    if (this.disposed) return;
    const token = ++this.readToken;
    try {
      const servers = await this.client.list();
      if (this.disposed || token !== this.readToken) return;
      this.patch({ servers, loadError: "" });
    } catch (error) {
      if (this.disposed || token !== this.readToken) return;
      this.patch({ loadError: `Could not load servers: ${failureMessage(error)}` });
    }
  }

  editForm(update: Partial<McpConnectForm>): void {
    this.patch({ form: { ...this.currentSnapshot.form, ...update } });
  }

  show(screen: McpScreen): void {
    const cleared =
      !this.pending &&
      (screen === "add" || (screen === "list" && this.currentSnapshot.screen === "add"));
    this.patch({
      error: "",
      screen,
      form: cleared ? emptyForm() : this.currentSnapshot.form,
    });
  }

  select(id: string): void {
    this.patch({ selectedId: id });
  }

  connect(): McpConnectProblem | null {
    const rejection = connectRejection(this.currentSnapshot.form);
    if (rejection) {
      this.patch({ error: rejection.message });
      return rejection.field;
    }
    if (this.disposed || this.pending) return null;
    void this.start(() => this.client.connect(connectRequest(this.currentSnapshot.form)));
    return null;
  }

  reconnect(serverId: string): void {
    if (!this.commandable) return;
    void this.start(() => this.client.reconnect(serverId));
  }

  async cancel(): Promise<void> {
    const operationId = this.currentSnapshot.operationId;
    if (!operationId || this.currentSnapshot.busy) return;
    if (this.currentSnapshot.operation?.state === "failed") {
      this.forgetOperation();
      this.patch({ operationId: null, operation: null, form: this.dropCredential(), error: "" });
      return;
    }
    this.patch({ busy: true, error: "" });
    try {
      await this.client.cancelOperation(operationId);
    } catch (error) {
      // A vanished operation has the same outcome as an acknowledged cancel.
      if (!(error instanceof RequestError && error.status === 404)) {
        this.patch({ busy: false, error: `Could not cancel connection: ${failureMessage(error)}` });
        return;
      }
    }
    this.forgetOperation();
    this.patch({ operationId: null, operation: null, form: this.dropCredential() });
    this.patch({ busy: false });
    void this.read();
  }

  async remove(serverId: string): Promise<void> {
    if (!this.commandable) return;
    this.patch({ busy: true, error: "" });
    try {
      await this.client.remove(serverId);
      this.patch({ screen: "list" });
      await this.read();
    } catch (error) {
      this.patch({ error: `Could not remove server: ${failureMessage(error)}` });
    } finally {
      this.patch({ busy: false });
    }
  }

  stopTracking(): void {
    this.forgetOperation();
    this.patch({
      operationId: null,
      operation: null,
      form: this.dropCredential(),
      error: "Connection status unknown. Check the server list.",
    });
    void this.read();
  }

  private async start(run: () => Promise<{ id: string }>): Promise<void> {
    this.patch({ busy: true, error: "", operation: null });
    try {
      const operationId = (await run()).id;
      this.patch({ operationId });
      const token = ++this.pollToken;
      void this.poll(operationId, token);
    } catch (error) {
      this.patch({ error: `Could not start connection: ${failureMessage(error)}` });
    } finally {
      this.patch({ busy: false });
    }
  }

  private async poll(operationId: string, token: number): Promise<void> {
    try {
      const operation = await this.client.getOperation(operationId);
      if (this.disposed || token !== this.pollToken) return;
      this.patch({ operation, error: "" });
      if (operation.state === "connected") {
        this.forgetOperation();
        this.patch({ operationId: null, operation: null, form: emptyForm(), screen: "list" });
        void this.read();
      } else if (operation.state !== "failed") {
        this.schedule(operationId, token, 1000);
      }
    } catch (error) {
      if (this.disposed || token !== this.pollToken) return;
      if (error instanceof RequestError && error.status === 404) {
        // The operation history is gone; the failed record tells the user what remains possible.
        this.patch({
          operation: {
            state: "failed",
            authorizationUrl: null,
            message: "Connection operation no longer available. Check the server list.",
          },
          error: "",
          form: this.dropCredential(),
        });
        void this.read();
      } else {
        this.patch({ error: `Could not check connection: ${failureMessage(error)}` });
        this.schedule(operationId, token, 3000);
      }
    }
  }

  private schedule(operationId: string, token: number, milliseconds: number): void {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => {
      this.timer = undefined;
      if (!this.disposed && token === this.pollToken) void this.poll(operationId, token);
    }, milliseconds);
  }

  private forgetOperation(): void {
    this.pollToken++;
    clearTimeout(this.timer);
    this.timer = undefined;
  }

  private dropCredential(): McpConnectForm {
    return { ...this.currentSnapshot.form, credential: "" };
  }

  private get pending(): boolean {
    return this.currentSnapshot.busy || this.currentSnapshot.operationId !== null;
  }

  private get commandable(): boolean {
    return !this.disposed && !this.pending;
  }

  private patch(update: Partial<McpSettingsSnapshot>): void {
    this.currentSnapshot = { ...this.currentSnapshot, ...update };
    for (const listener of this.listeners) listener();
  }
}

function emptyForm(): McpConnectForm {
  return { url: "", authentication: "automatic", headerName: "", credential: "" };
}

function failureMessage(error: unknown): string {
  return error instanceof Error ? error.message : "Request failed.";
}
