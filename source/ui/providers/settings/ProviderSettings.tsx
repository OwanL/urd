import { useCallback, useLayoutEffect, useRef, useState } from "preact/hooks";
import { Icon } from "../../components/Icon.tsx";
import type { ProviderConnection, ProviderRoute } from "../ProviderConnection.ts";
import { ConnectionForm } from "./ConnectionForm.tsx";
import { ProviderChooser } from "./ProviderChooser.tsx";
import { ProviderConnectionDetail } from "./ProviderConnectionDetail.tsx";
import { ProviderSettingsFeedback } from "./ProviderSettingsFeedback.tsx";
import { ProviderSettingsOverview } from "./ProviderSettingsOverview.tsx";
import { connectionStatus, connectionTitle } from "./ProviderSettingsPresentation.ts";
import { matchesConnection, providerSetupState } from "./ProviderSettingsController.ts";
import type { ProviderSettingsState } from "./useProviderSettings.ts";
import "./provider-settings.css";

interface Presentation {
  screen: "overview" | "chooser" | "custom" | "detail";
  connectionId: string | null;
  creating: ReadonlySet<string> | null;
  returnId: string | null;
  setup?: boolean;
}

// Settings unmounts its content when closed. Keep navigation with its command owner.
const presentations = new WeakMap<ProviderSettingsState["actions"], Presentation>();

export function ProviderSettings({ state, search }: ProviderSettingsProps) {
  const { actions } = state;
  const heading = useRef<HTMLHeadingElement>(null);
  const surface = useRef<HTMLElement>(null);
  const overviewControl = useRef<HTMLButtonElement>(null);
  const focusRequested = useRef(false);
  const previousSearch = useRef(search);
  const [presentation, setPresentation] = useState<Presentation>(
    () =>
      presentations.get(actions) ?? {
        screen: "overview",
        connectionId: null,
        creating: null,
        returnId: null,
      },
  );
  const { screen, connectionId, creating, returnId } = presentation;
  const show = useCallback(
    (next: Presentation, focus = true) => {
      focusRequested.current = focus;
      presentations.set(actions, next);
      setPresentation(next);
    },
    [actions],
  );
  const connection = state.connections.find(({ id }) => id === connectionId);

  useLayoutEffect(() => {
    if (search === previousSearch.current) return;
    previousSearch.current = search;
    if (search.trim() && screen !== "overview") {
      actions.select(null);
      show({ screen: "overview", connectionId: null, creating: null, returnId: null }, false);
    }
  }, [search, screen, actions, show]);

  useLayoutEffect(() => {
    // Only a successful create selects a new ID. Rejections and uncertain reads stay put.
    if (
      creating &&
      state.selectedId &&
      !creating.has(state.selectedId) &&
      state.connections.some((item) => item.id === state.selectedId)
    )
      show({
        screen: "detail",
        connectionId: state.selectedId,
        creating: null,
        returnId: null,
        setup: true,
      });
  }, [creating, state.selectedId, state.connections, show]);

  useLayoutEffect(() => {
    if (screen === "detail" && !connection && state.loaded && !state.pending)
      show({ screen: "overview", connectionId: null, creating: null, returnId: null });
  }, [screen, connection, state.loaded, state.pending, show]);

  useLayoutEffect(() => {
    if (!focusRequested.current) return;
    focusRequested.current = false;
    if (screen === "overview") {
      const control = overviewControl.current;
      (control && !control.disabled ? control : surface.current)?.focus();
    } else heading.current?.focus();
  }, [presentation, screen]);

  if (!state.active) return null;
  const complete = state.connections.filter(
    (item) => providerSetupState(item, state.credentials[item.id]) === "complete",
  );
  const unfinished = state.connections.filter(
    (item) => providerSetupState(item, state.credentials[item.id]) === "incomplete",
  );
  const unknown = state.connections.filter(
    (item) => providerSetupState(item, state.credentials[item.id]) === "unknown",
  );
  const visible = complete.filter((item) => matchesConnection(item, search));
  const setup = connection && providerSetupState(connection, state.credentials[connection.id]);
  const canEdit = state.pending === null && !state.uncertain && state.confirmation === null;
  const canNavigate = state.confirmation === null;
  const overview = () => {
    actions.select(null);
    show({ screen: "overview", connectionId: null, creating: null, returnId: connectionId });
  };
  const chooser = () => {
    actions.select(null);
    show({ screen: "chooser", connectionId: null, creating: null, returnId: null });
  };
  const open = (item: ProviderConnection, setup = false) => {
    actions.select(item.id);
    show({
      screen: "detail",
      connectionId: item.id,
      creating: null,
      returnId: item.id,
      setup,
    });
  };
  const backToChooser = screen === "custom" || (screen === "detail" && setup !== "complete");
  const create = () => {
    if (!canEdit) return;
    show({ ...presentation, creating: new Set(state.connections.map(({ id }) => id)) });
    actions.select(null);
    void actions.create();
  };
  const choose = (route: ProviderRoute) => {
    actions.editAdd({ route });
    if (route !== "custom") create();
    else {
      actions.select(null);
      show({ screen: "custom", connectionId: null, creating: null, returnId: null });
    }
  };

  return (
    <section
      ref={surface}
      tabIndex={-1}
      class="provider-settings"
      aria-label="Providers"
      aria-busy={state.reading}
    >
      {screen !== "overview" && (
        <button
          type="button"
          class="provider-settings-back"
          disabled={!canNavigate}
          onClick={backToChooser ? chooser : overview}
        >
          <Icon name="chevron" />
          {backToChooser ? "Back to add provider" : "Back to providers"}
        </button>
      )}

      {screen === "overview" ? (
        <ProviderSettingsOverview
          state={state}
          visible={visible}
          complete={complete}
          unknown={unknown}
          canEdit={canEdit}
          canNavigate={canNavigate}
          returnId={returnId}
          overviewControl={overviewControl}
          onAddProvider={chooser}
          onOpenConnection={open}
        />
      ) : (
        <>
          <header class="provider-settings-panel-heading">
            <h3 ref={heading} tabIndex={-1}>
              {screen === "chooser"
                ? "Add provider"
                : screen === "custom"
                  ? "Custom provider"
                  : connection
                    ? connectionTitle(connection)
                    : "Provider unavailable"}
            </h3>
            {connection && (
              <p class="provider-settings-secondary">{connectionStatus(state, connection)}</p>
            )}
          </header>
          {screen === "chooser" && (
            <ProviderChooser
              state={state}
              unfinished={unfinished}
              unknown={unknown}
              canEdit={canEdit}
              canNavigate={canNavigate}
              onChoose={choose}
              onOpenConnection={open}
            />
          )}
          {screen === "custom" && (
            <ConnectionForm state={state} disabled={!canEdit} onCreate={create} />
          )}
          {screen === "detail" && connection && (
            <>
              <ProviderConnectionDetail
                key={connection.id}
                state={state}
                connection={connection}
                disabled={!canEdit}
              />
              {presentation.setup && setup === "complete" && (
                <div class="provider-settings-actions">
                  <button type="button" disabled={!canNavigate} onClick={overview}>
                    Done
                  </button>
                </div>
              )}
            </>
          )}
        </>
      )}

      <ProviderSettingsFeedback state={state} />
    </section>
  );
}

interface ProviderSettingsProps {
  state: ProviderSettingsState;
  search: string;
}
