import { RequestError } from "../../http/RequestError.ts";
import type { ProviderCredentialStatus } from "../ProviderCredentials.ts";
import type { ProviderSignIn } from "../ProviderSignIn.ts";
import { reconcileConnections, type ConnectionReadState } from "./ProviderConnectionRecords.ts";
import { signInPending } from "./ProviderConnectionStatus.ts";
import type { ProviderSettingsClient, ProviderSettingsSnapshot } from "./ProviderSettingsTypes.ts";

type ReadClient = Pick<
  ProviderSettingsClient,
  "list" | "getCredentials" | "getModels" | "getModelSettings" | "getSignIn"
>;

type ReadUpdate = Partial<
  Omit<ConnectionReadState, "uncertain"> &
    Pick<
      ProviderSettingsSnapshot,
      "connectionsReloadedAfterUncertainty" | "apiKey" | "confirmation"
    >
>;

// One pass through the read endpoints; the controller owns freshness, cancellation and polling.
interface ReadCycle {
  readonly signal: AbortSignal;
  readonly isCurrent: () => boolean;
  readonly snapshot: () => ConnectionReadState;
  readonly publish: (update: ReadUpdate) => void;
  readonly uncertainModelConnectionId: () => string | null;
  readonly forgetSetupSignIn: (id: string) => void;
  readonly captureSignInCompletion: (
    id: string,
  ) => (signIn: ProviderSignIn, credentials: ProviderCredentialStatus | undefined) => void;
}

function readFailure(error: unknown): string {
  return error instanceof RequestError
    ? `Read failed (HTTP ${error.status}). Previous information is retained.`
    : "Read failed. Previous information is retained.";
}

export async function readProviderSettings(client: ReadClient, cycle: ReadCycle): Promise<void> {
  const { signal, isCurrent, snapshot, publish } = cycle;
  const setError = (key: string, message: string) =>
    publish({ errors: { ...snapshot().errors, [key]: message } });
  const clearError = (key: string) => {
    if (!(key in snapshot().errors)) return;
    const errors = { ...snapshot().errors };
    delete errors[key];
    publish({ errors });
  };
  const storeCredentials = (status: ProviderCredentialStatus) =>
    publish({ credentials: { ...snapshot().credentials, [status.connectionId]: status } });
  const storeSignIn = (signIn: ProviderSignIn) =>
    publish({ signIns: { ...snapshot().signIns, [signIn.connectionId]: signIn } });
  const observe = async <T>(key: string, request: () => Promise<T>, apply: (value: T) => void) => {
    try {
      const value = await request();
      if (!isCurrent()) return;
      apply(value);
      clearError(key);
    } catch (error) {
      if (isCurrent()) setError(key, readFailure(error));
    }
  };
  const observeCatalog = (id: string) =>
    observe(
      `models:${id}`,
      () => client.getModels(id, signal),
      (catalog) =>
        publish({ catalogs: { ...snapshot().catalogs, [catalog.connectionId]: catalog } }),
    );
  const observeSignIn = async (previous: ProviderSignIn) => {
    const key = `signIn:${previous.connectionId}`;
    let next: ProviderSignIn;
    try {
      next = await client.getSignIn(previous.id, signal);
      if (!isCurrent()) return;
      if (signInPending(next)) storeSignIn(next);
      clearError(key);
    } catch (error) {
      if (!isCurrent()) return;
      if (error instanceof RequestError && error.status === 404) {
        const signIns = { ...snapshot().signIns };
        delete signIns[previous.connectionId];
        cycle.forgetSetupSignIn(previous.id);
        publish({ signIns });
        setError(key, "This sign-in is no longer available. Start sign-in again.");
      } else setError(key, readFailure(error));
      return;
    }
    if (signInPending(next)) return;
    const complete = cycle.captureSignInCompletion(previous.id);
    let credentials: ProviderCredentialStatus | undefined;
    try {
      credentials = await client.getCredentials(next.connectionId, signal);
      if (!isCurrent()) return;
      storeCredentials(credentials);
      clearError(`credentials:${next.connectionId}`);
    } catch (error) {
      if (!isCurrent()) return;
      setError(`credentials:${next.connectionId}`, readFailure(error));
    }
    if (
      next.status === "completed" &&
      snapshot().selectedId === next.connectionId &&
      snapshot().connections.some(
        ({ id, route }) => id === next.connectionId && route === "gitHubCopilot",
      )
    ) {
      // The catalog read earlier in this pass may have preceded Copilot's sign-in refresh.
      await observeCatalog(next.connectionId);
      if (!isCurrent()) return;
    }
    storeSignIn(next);
    complete(next, credentials);
  };

  await observe(
    "connections",
    () => client.list(signal),
    (connections) =>
      publish(reconcileConnections(snapshot(), connections, cycle.uncertainModelConnectionId())),
  );
  if (!isCurrent()) return;
  const jobs = snapshot().connections.flatMap(({ id }) => [
    observe(`credentials:${id}`, () => client.getCredentials(id, signal), storeCredentials),
    observe(
      `modelSettings:${id}`,
      () => client.getModelSettings(id, signal),
      (settings) => publish({ modelSettings: { ...snapshot().modelSettings, [id]: settings } }),
    ),
  ]);
  const { selectedId } = snapshot();
  if (selectedId) jobs.push(observeCatalog(selectedId));
  await Promise.all(jobs);
  if (!isCurrent()) return;
  const uncertainId = cycle.uncertainModelConnectionId();
  const { uncertain, errors, modelSettings } = snapshot();
  if (
    uncertain &&
    uncertainId &&
    !errors.connections &&
    !errors[`modelSettings:${uncertainId}`] &&
    modelSettings[uncertainId]
  )
    publish({ connectionsReloadedAfterUncertainty: true });
  await Promise.all(Object.values(snapshot().signIns).filter(signInPending).map(observeSignIn));
}
