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
import { useModelOptions } from "../providers/models/useModelOptions.ts";
import { ProviderSettings } from "../providers/settings/ProviderSettings.tsx";
import { useProviderSettings } from "../providers/settings/useProviderSettings.ts";
import { ModelBucketsPanel } from "../settings/ModelBucketsPanel.tsx";
import { WorkingDirectorySetting } from "../settings/WorkingDirectorySetting.tsx";
import "../settings/settings.css";
import "./setup.css";

interface SetupStatus {
  required: boolean;
  providerConfigured: boolean;
  bucketsConfigured: boolean;
  workingDirectoryConfigured: boolean;
}

export type SetupState = "checking" | "required" | "complete";

const endpoint = new URL("/api/setup", window.location.href);
const buckets = ["small", "medium", "large"] as const;
const titles = ["Set up a provider", "Assign models", "Default working directory"];

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
  const [step, setStep] = useState(() =>
    !status.providerConfigured ? 0 : !status.bucketsConfigured ? 1 : 2,
  );
  const [validated, setValidated] = useState(status);
  const [busy, setBusy] = useState(false);
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
    true,
    models.providerReady,
    preferences.refresh,
  );
  // The server validates each step again; this only avoids offering an action that must fail.
  const canContinue = [
    providers.connections.length > 0,
    buckets.every((bucket) => preferences.preferences?.modelChoices[bucket].length),
    validated.workingDirectoryConfigured,
  ][step];
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
      if (step === 0 && !current.providerConfigured) {
        setError("Add a provider to continue.");
        return;
      }
      if (step > 0 && !current.providerConfigured) {
        setStep(0);
        setError("Add a provider to continue.");
        return;
      }
      if (step === 1 && !current.bucketsConfigured) {
        setError("Add a model to each bucket to continue.");
        return;
      }
      if (step < 2) {
        setStep(step + 1);
        return;
      }
      if (
        !current.providerConfigured ||
        !current.bucketsConfigured ||
        !current.workingDirectoryConfigured
      ) {
        setStep(!current.providerConfigured ? 0 : !current.bucketsConfigured ? 1 : 2);
        setError("Complete each setup step before continuing.");
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
        step === 2
          ? "Setup could not be confirmed. Try again."
          : "Setup status could not be loaded. Try again.",
      );
    } finally {
      setBusy(false);
    }
  };
  return (
    <SetupModal title={titles[step]!} progress={`${step + 1} of 3`}>
      <div class="setup-content">
        {step === 0 && (
          <ProviderSettings
            state={{ ...providers, bucketSetup: null }}
            search=""
            initialScreen={validated.providerConfigured ? "overview" : "chooser"}
          />
        )}
        {step === 1 && <ModelBucketsPanel client={providerClient} state={preferences} active />}
        {step === 2 && (
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
      </div>
      {error && (
        <p class="setup-error" role="alert">
          {error}
        </p>
      )}
      <footer class="setup-actions">
        {step > 0 && (
          <button
            type="button"
            disabled={busy}
            onClick={() => {
              setError(null);
              setStep(step - 1);
            }}
          >
            Back
          </button>
        )}
        <button
          class="setup-primary"
          type="button"
          disabled={!canContinue || busy || preferences.pending || providers.pending !== null}
          onClick={() => void next()}
        >
          {busy ? "Checking…" : step === 2 ? "Finish" : "Next"}
        </button>
      </footer>
    </SetupModal>
  );
}
