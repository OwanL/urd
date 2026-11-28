import type { ComponentChildren } from "preact";
import {
  useCallback,
  useEffect,
  useId,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from "preact/hooks";
import { requestJson } from "../http/Requests.ts";
import { PreferencesClient } from "../preferences/PreferencesClient.ts";
import { usePreferences } from "../preferences/usePreferences.ts";
import { ProviderClient } from "../providers/ProviderClient.ts";
import { ModelFavoritesProvider } from "../providers/models/ModelFavorites.tsx";
import { useModelOptions } from "../providers/models/useModelOptions.ts";
import { ProviderSettings } from "../providers/settings/ProviderSettings.tsx";
import {
  credentialState,
  providerReady,
  signInPending,
} from "../providers/settings/ProviderConnectionStatus.ts";
import { useProviderSettings } from "../providers/settings/useProviderSettings.ts";
import { SetupModels } from "./SetupModels.tsx";
import {
  missingSetupStep,
  nextSetupStep,
  setupSteps,
  type SetupStatus,
  type SetupStep,
} from "./SetupSteps.ts";
import { SourceIgnorePicker } from "./SourceIgnorePicker.tsx";
import { WorkingDirectorySetting } from "../settings/WorkingDirectorySetting.tsx";
import "../settings/settings.css";
import "./setup.css";

export type SetupState = "checking" | "required" | "complete";

const endpoint = new URL("/api/setup", window.location.href);
const buckets = ["small", "medium", "large"] as const;
const titles = {
  directory: "Default working directory",
  provider: "Set up a provider",
  models: "Assign models",
  instructions: "Instructions",
  skills: "Skills",
};
const missingMessage = {
  directory: "Choose an existing working directory to continue.",
  provider: "No provider with available models.",
  models: "Add a model to each bucket to continue.",
};

// Fresh profiles configure Urd in a modal over the inert workspace.
export function SetupDialog({
  state,
  onStateChange,
}: {
  state: SetupState;
  onStateChange(state: SetupState): void;
}) {
  const [status, setStatus] = useState<SetupStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const read = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const current = await requestJson<SetupStatus>(endpoint, "GET");
      setStatus(current);
      onStateChange(current.required ? "required" : "complete");
    } catch {
      setError("Setup status could not be loaded.");
    } finally {
      setLoading(false);
    }
  }, [onStateChange]);
  useEffect(() => {
    void read();
  }, [read]);
  if (state === "complete") return null;
  if (status) return <Setup status={status} onComplete={() => onStateChange("complete")} />;
  if (!error) return null;
  return (
    <SetupModal title="Setup">
      <div class="setup-content">
        <p role="alert">{error}</p>
      </div>
      <footer class="setup-actions">
        <button class="setup-primary" type="button" onClick={() => void read()} disabled={loading}>
          Retry
        </button>
      </footer>
    </SetupModal>
  );
}

// Setup cannot be dismissed; it closes by unmounting once the server confirms completion.
function SetupModal({
  title,
  progress,
  children,
}: {
  title: string;
  progress?: string;
  children: ComponentChildren;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const shownTitle = useRef(title);
  const titleId = useId();
  useEffect(() => {
    // Window listeners run after nested popovers handle Escape, then stop the dialog close request.
    const keepOpen = (event: KeyboardEvent) => {
      if (event.key === "Escape") event.preventDefault();
    };
    window.addEventListener("keydown", keepOpen);
    return () => window.removeEventListener("keydown", keepOpen);
  }, []);
  useLayoutEffect(() => {
    if (!dialog.current?.open) dialog.current?.showModal();
    if (shownTitle.current === title) return;
    shownTitle.current = title;
    heading.current?.focus();
  });
  return (
    <dialog
      ref={dialog}
      class="setup-dialog"
      aria-labelledby={titleId}
      onCancel={(event) => event.preventDefault()}
      onClose={() => dialog.current?.showModal()}
    >
      <header class="setup-header">
        <h2 id={titleId} ref={heading} tabIndex={-1}>
          {title}
        </h2>
        {progress && <span>{progress}</span>}
      </header>
      {children}
    </dialog>
  );
}

function Setup({ status, onComplete }: { status: SetupStatus; onComplete(): void }) {
  const [step, setStep] = useState<SetupStep>(() => missingSetupStep(status) ?? "instructions");
  const index = setupSteps.indexOf(step);
  const [validated, setValidated] = useState(status);
  const [editModels, setEditModels] = useState(status.bucketsConfigured);
  const [busy, setBusy] = useState(false);
  const [pickerBusy, setPickerBusy] = useState(false);
  const [suggestionsBusy, setSuggestionsBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const folderButton = useRef<HTMLButtonElement>(null);
  const providerClient = useMemo(() => new ProviderClient(new URL(window.location.href)), []);
  const preferenceClient = useMemo(() => new PreferencesClient(new URL(window.location.href)), []);
  const preferences = usePreferences(preferenceClient);
  const models = useModelOptions(providerClient);
  const providers = useProviderSettings(
    providerClient,
    true,
    models.providersChanged,
    step === "provider",
    models.providerReady,
    preferences.refresh,
    false, // The model step owns the offer during onboarding.
  );
  const readyProvider = providers.connections.some((connection) => {
    const signIn = providers.signIns[connection.id];
    if (signIn && signInPending(signIn)) return false;
    if (providers.errors[`credentials:${connection.id}`]) return false;
    if (!providerReady(connection, providers.credentials[connection.id])) return false;
    return (
      !!providers.catalogs[connection.id]?.models.length ||
      models.options.some(({ selection }) => selection.connection.id === connection.id)
    );
  });
  const connectedProvider = providers.connections.some((connection) => {
    const credential = credentialState(connection, providers.credentials[connection.id]);
    return credential === "connected" || credential === "noAuthentication";
  });
  const awaitingModels = step === "provider" && connectedProvider && !readyProvider;
  const { retry: discoverModels, loading, refreshing } = models;
  const retryDelay = useRef(2000);
  useEffect(() => {
    if (!awaitingModels) {
      retryDelay.current = 2000;
      return;
    }
    if (loading || refreshing) return;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const schedule = () => {
      clearTimeout(timer);
      if (!document.hidden)
        timer = setTimeout(() => {
          retryDelay.current = Math.min(30000, retryDelay.current * 2);
          void discoverModels();
        }, retryDelay.current);
    };
    schedule();
    document.addEventListener("visibilitychange", schedule);
    return () => {
      clearTimeout(timer);
      document.removeEventListener("visibilitychange", schedule);
    };
  }, [awaitingModels, loading, refreshing, discoverModels]);
  const providerReadFailed =
    !!providers.errors.connections ||
    providers.connections.some(
      (connection) =>
        connection.authentication !== "none" && !!providers.errors[`credentials:${connection.id}`],
    );
  // The server validates each step again; this only avoids offering an action that must fail.
  const canContinue = {
    directory: validated.workingDirectoryConfigured,
    provider: readyProvider && !providers.uncertain && !providers.errors.connections,
    models: buckets.every((bucket) => preferences.preferences?.modelChoices[bucket].length),
    instructions: true,
    skills: true,
  }[step];
  const next = async () => {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      const current = await requestJson<SetupStatus>(endpoint, "GET");
      setValidated(current);
      if (!current.required) {
        onComplete();
        return;
      }
      const following = nextSetupStep(step, current);
      if (following) {
        if (
          following !== "instructions" &&
          following !== "skills" &&
          setupSteps.indexOf(following) <= index
        ) {
          if (following === "models") setEditModels(true);
          setError(missingMessage[following]);
        }
        setStep(following);
        return;
      }
      const result = await requestJson<SetupStatus>(
        new URL("/api/setup/complete", endpoint),
        "POST",
        {},
      );
      if (result.required) {
        setError("Setup could not be confirmed. Try again.");
        return;
      }
      onComplete();
    } catch {
      setError(
        step === "skills"
          ? "Setup could not be confirmed. Try again."
          : "Setup status could not be loaded. Try again.",
      );
    } finally {
      setBusy(false);
    }
  };
  const content = (
    <SetupModal title={titles[step]} progress={`${index + 1} of ${setupSteps.length}`}>
      <div class="setup-content">
        {step === "provider" && (
          <ProviderSettings
            state={providers}
            initialScreen={validated.providerConfigured ? "overview" : "chooser"}
            onboarding
          />
        )}
        {awaitingModels && (models.loading || models.refreshing) && (
          <p role="status" class="provider-settings-secondary">
            Discovering models…
          </p>
        )}
        {awaitingModels && !models.loading && !models.refreshing && (
          <p role="status" class="provider-settings-secondary">
            {models.error ? "Could not load models." : "No models available yet."} Retrying…
          </p>
        )}
        {step === "provider" && providerReadFailed && !canContinue && (
          <button
            type="button"
            disabled={providers.reading}
            onClick={() => void providers.actions.read()}
          >
            Retry provider status
          </button>
        )}
        {step === "models" && (
          <SetupModels
            client={providerClient}
            state={preferences}
            catalogLoading={models.loading || models.refreshing}
            editing={editModels}
            onEdit={() => setEditModels(true)}
            onSaved={next}
            onBusyChange={setSuggestionsBusy}
          />
        )}
        {step === "directory" && (
          <div class="general-settings">
            <WorkingDirectorySetting
              state={preferences}
              buttonRef={folderButton}
              emptyLabel="Not chosen"
              resettable={false}
              chosen={validated.workingDirectoryConfigured}
              onChoose={async (path) => {
                const saved = await preferences.saveWorkingDirectory(path);
                if (saved)
                  setValidated((current) => ({ ...current, workingDirectoryConfigured: true }));
                return saved;
              }}
            />
          </div>
        )}
        {step === "instructions" && (
          <SourceIgnorePicker key="instructions" kind="instructions" onBusyChange={setPickerBusy} />
        )}
        {step === "skills" && (
          <SourceIgnorePicker key="skills" kind="skills" onBusyChange={setPickerBusy} />
        )}
      </div>
      {error && (
        <p class="setup-error" role="alert">
          {error}
        </p>
      )}
      <footer class="setup-actions">
        {index > 0 && (
          <button
            type="button"
            disabled={busy || pickerBusy || suggestionsBusy}
            onClick={() => {
              setError(null);
              setStep(setupSteps[index - 1]!);
            }}
          >
            Back
          </button>
        )}
        {(step === "instructions" || step === "skills") && (
          <button type="button" disabled={busy || pickerBusy} onClick={() => void next()}>
            Skip
          </button>
        )}
        <button
          class="setup-primary"
          type="button"
          disabled={
            !canContinue ||
            busy ||
            pickerBusy ||
            suggestionsBusy ||
            preferences.pending ||
            providers.pending !== null
          }
          onClick={() => void next()}
        >
          {busy ? "Checking…" : step === "skills" ? "Finish" : "Next"}
        </button>
      </footer>
    </SetupModal>
  );
  return <ModelFavoritesProvider preferences={preferences}>{content}</ModelFavoritesProvider>;
}
