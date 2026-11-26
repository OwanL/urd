import type { ProviderConnection } from "../ProviderConnection.ts";
import type { ProviderCredentialStatus } from "../ProviderCredentials.ts";
import type { ProviderSignIn } from "../ProviderSignIn.ts";
import { credentialState, signInPending } from "./ProviderConnectionStatus.ts";
import type { credentialCommands } from "./ProviderCredentialCommands.ts";
import type { ProviderSettingsSnapshot } from "./ProviderSettingsTypes.ts";
import { authorizationLink, reserveSignInTab } from "./ProviderSignInTab.ts";

interface SignInContext {
  readonly snapshot: () => Pick<
    ProviderSettingsSnapshot,
    "add" | "connections" | "selectedId" | "signIns" | "blockedSignIns"
  >;
  readonly publish: (update: Pick<ProviderSettingsSnapshot, "blockedSignIns">) => void;
  readonly activity: () => {
    readonly canMutate: boolean;
    readonly visible: boolean;
    readonly setupVisible: boolean;
    readonly setupView: number;
    readonly disposed: boolean;
  };
  readonly createConnection: (
    created: (connection: ProviderConnection, view: number) => void,
  ) => Promise<void>;
  readonly commands: Pick<ReturnType<typeof credentialCommands>, "beginSignIn" | "cancelSignIn">;
  readonly notifyProviderReady: (connection: ProviderConnection, view: number) => boolean;
  readonly changed: () => void;
}

// Keeps ownership of blank tabs until they navigate, and tracks setup sign-ins until completion.
export function providerSignInTabs({
  snapshot,
  publish,
  activity,
  createConnection,
  commands,
  notifyProviderReady,
  changed,
}: SignInContext) {
  let signInIntent = 0;
  let openingTab = false;
  const reservations = new Set<Window>();
  const setupSignIns = new Map<string, { connection: ProviderConnection; view: number }>();

  function onHidden(): void {
    if (!openingTab && reservations.size === 0) signInIntent++;
  }

  function onConnectionEdited(id: string): void {
    if (snapshot().selectedId === id) signInIntent++;
  }

  function reserveTab(): Window | null {
    openingTab = true;
    try {
      const tab = reserveSignInTab();
      if (tab) reservations.add(tab);
      return tab;
    } finally {
      openingTab = false;
    }
  }

  function closeReservation(tab: Window | null): void {
    if (tab && reservations.delete(tab)) tab.close();
  }

  function dispose(): void {
    for (const tab of reservations) closeReservation(tab);
    setupSignIns.clear();
  }

  async function createAndSignIn(): Promise<void> {
    const { canMutate, setupVisible, visible } = activity();
    const { selectedId, add } = snapshot();
    if (!canMutate || !setupVisible || !visible || selectedId !== null || add.route !== "chatGpt")
      return;

    const tab = reserveTab();
    const existingIds = new Set(snapshot().connections.map(({ id }) => id));
    const intent = signInIntent;
    let signIn: Promise<void> | undefined;
    await createConnection((connection, view) => {
      const { connections, selectedId } = snapshot();
      const saved = connections.find(({ id }) => id === connection.id);
      const { canMutate, setupVisible, visible, setupView } = activity();
      if (
        existingIds.has(connection.id) ||
        connection.route !== "chatGpt" ||
        connection.authentication !== "oAuth" ||
        !canMutate ||
        !setupVisible ||
        (!visible && !tab) ||
        setupView !== view ||
        signInIntent !== intent ||
        selectedId !== connection.id ||
        !saved ||
        saved.name !== connection.name ||
        saved.route !== connection.route ||
        saved.api !== connection.api ||
        saved.endpoint !== connection.endpoint ||
        saved.authentication !== connection.authentication
      )
        return;
      signIn = startSignIn(connection, true, tab);
    });
    if (!signIn) closeReservation(tab);
    await signIn;
  }

  function beginSignIn(connection: ProviderConnection, setup: boolean): Promise<void> {
    if (
      !activity().canMutate ||
      Object.values(snapshot().signIns).some(
        (item) => item.connectionId === connection.id && signInPending(item),
      )
    )
      return Promise.resolve();
    return startSignIn(connection, setup, reserveTab());
  }

  async function startSignIn(
    connection: ProviderConnection,
    setup: boolean,
    tab: Window | null,
  ): Promise<void> {
    const view = activity().setupView;
    let started = false;
    try {
      await commands.beginSignIn(
        connection,
        setup
          ? (id) => {
              if (!activity().disposed) setupSignIns.set(id, { connection, view });
            }
          : undefined,
        (signIn) => {
          started = true;
          if (activity().disposed) {
            closeReservation(tab);
            return;
          }
          const blocked = finishReservation(tab, signIn);
          publish({ blockedSignIns: { ...snapshot().blockedSignIns, [connection.id]: blocked } });
        },
      );
    } finally {
      if (!started) closeReservation(tab);
    }
  }

  function finishReservation(tab: Window | null, signIn: ProviderSignIn): boolean {
    if (!tab) return true;
    const url = authorizationLink(signIn.authorizationUrl);
    if (signInPending(signIn) && url && reservations.has(tab)) {
      try {
        tab.location.href = url;
        reservations.delete(tab);
        return false;
      } catch {
        closeReservation(tab);
        return true;
      }
    }
    closeReservation(tab);
    return false;
  }

  function forgetSetupSignIn(id: string): void {
    setupSignIns.delete(id);
  }

  function cancelSignIn(signIn: ProviderSignIn): Promise<void> {
    return commands.cancelSignIn(signIn, forgetSetupSignIn);
  }

  function captureSignInCompletion(id: string) {
    const context = setupSignIns.get(id);
    return (next: ProviderSignIn, credentials: ProviderCredentialStatus | undefined) => {
      forgetSetupSignIn(id);
      if (
        context &&
        next.status === "completed" &&
        credentials &&
        credentialState(context.connection, credentials) === "connected" &&
        notifyProviderReady(context.connection, context.view)
      )
        return;
      changed();
    };
  }

  return {
    onHidden,
    onConnectionEdited,
    dispose,
    createAndSignIn,
    beginSignIn,
    cancelSignIn,
    forgetSetupSignIn,
    captureSignInCompletion,
  };
}
