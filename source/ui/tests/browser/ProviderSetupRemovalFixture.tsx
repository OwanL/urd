import { render } from "preact";
import { useMemo, useState } from "preact/hooks";
import "../../base.css";
import type { ProviderConnection } from "../../providers/ProviderConnection.ts";
import { ProviderSettings } from "../../providers/settings/ProviderSettings.tsx";
import type { Confirmation } from "../../providers/settings/ProviderSettingsTypes.ts";
import type { ProviderSettingsState } from "../../providers/settings/useProviderSettings.ts";

const unfinished: ProviderConnection = {
  id: "unfinished-openai",
  name: "OpenAI",
  route: "openAI",
  api: "openAIResponses",
  endpoint: "https://api.openai.com/v1",
  authentication: "apiKey",
};

function Fixture() {
  const [connections, setConnections] = useState<readonly ProviderConnection[]>([unfinished]);
  const [confirmation, setConfirmation] = useState<Confirmation | null>(null);
  const actions = useMemo(
    () => ({
      select: () => {},
      chooseProvider: () => {},
      requestConfirmation: (kind: Confirmation["kind"], connection: ProviderConnection) =>
        setConfirmation({ kind, connection }),
      dismissConfirmation: () => setConfirmation(null),
      confirm: async () => {
        setConnections((current) => current.filter(({ id }) => id !== unfinished.id));
        setConfirmation(null);
      },
    }),
    [],
  );
  const state = {
    connections,
    selectedId: null,
    loaded: true,
    reading: false,
    pending: null,
    notice: null,
    uncertain: false,
    connectionsReloadedAfterUncertainty: false,
    errors: {},
    credentials: {
      [unfinished.id]: {
        connectionId: unfinished.id,
        hasCredentials: false,
        requiresSignIn: false,
        accountLabel: null,
        expiresAt: null,
        planAccessGranted: null,
        isDisconnecting: false,
      },
    },
    catalogs: {},
    modelSettings: {},
    modelDrafts: {},
    signIns: {},
    blockedSignIns: {},
    drafts: {},
    add: {
      choice: "openAI",
      endpoint: unfinished.endpoint,
      api: unfinished.api,
      authentication: "apiKey",
    },
    apiKey: "",
    confirmation,
    bucketSetup: null,
    active: true,
    actions,
  } as unknown as ProviderSettingsState;

  return (
    <main>
      <ProviderSettings state={state} initialScreen="chooser" />
      <output aria-label="Saved connection count">{connections.length}</output>
    </main>
  );
}

render(<Fixture />, document.body);
