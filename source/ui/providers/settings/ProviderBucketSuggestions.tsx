import type { BucketSetup } from "./ProviderSettingsController.ts";

export function ProviderBucketSuggestions({
  setup,
  disabled,
  onSkip,
  onConfirm,
}: {
  setup: BucketSetup;
  disabled: boolean;
  onSkip: () => void;
  onConfirm: () => void;
}) {
  if (setup.phase === "finished")
    return (
      <p role="status" class={setup.error ? "provider-settings-error" : ""}>
        {setup.message}
      </p>
    );

  return (
    <section class="provider-settings-group" aria-label="Suggested model buckets">
      <h4>Suggested model buckets</h4>
      {setup.phase === "loading" ? (
        <p role="status" class="provider-settings-secondary">
          Checking for suggestions…
        </p>
      ) : (
        <>
          <p class="provider-settings-secondary" title={setup.proposal.connection.id}>
            {setup.proposal.connection.name} · {new URL(setup.proposal.connection.endpoint).host}
          </p>
          <dl class="provider-settings-bucket-list">
            {setup.proposal.assignments.map(({ bucket, modelId }) => (
              <div key={bucket}>
                <dt>{bucket}</dt>
                <dd>{modelId}</dd>
              </div>
            ))}
          </dl>
        </>
      )}
      <div class="provider-settings-actions">
        {setup.phase !== "loading" && (
          <button
            type="button"
            class="provider-settings-primary"
            disabled={disabled || setup.phase === "saving"}
            onClick={onConfirm}
          >
            {setup.phase === "saving" ? "Saving…" : "Use suggested buckets"}
          </button>
        )}
        <button type="button" disabled={setup.phase === "saving"} onClick={onSkip}>
          Skip
        </button>
      </div>
    </section>
  );
}
