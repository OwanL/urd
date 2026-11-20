import { useCallback, useEffect, useRef, useState } from "preact/hooks";
import type { ProviderClient } from "../providers/ProviderClient.ts";
import {
  offerBucketsAfterRefresh,
  saveBucketSuggestions,
} from "../providers/settings/ProviderBucketSetup.ts";
import type { BucketSetup } from "../providers/settings/ProviderSettingsTypes.ts";

const loading: BucketSetup = { phase: "loading" };
const unavailable: BucketSetup = {
  phase: "finished",
  message: "Could not load suggested models.",
  error: true,
};

/** Finds the first saved provider able to suggest models for every bucket. */
export function useBucketSuggestions(
  client: ProviderClient,
  refreshPreferences: () => Promise<void> | undefined,
  enabled: boolean,
) {
  const [setup, setSetup] = useState<BucketSetup | null>(loading);
  const [attempt, setAttempt] = useState(0);
  const request = useRef<AbortController | null>(null);

  useEffect(() => {
    if (!enabled) {
      setSetup(loading);
      return;
    }
    const abort = new AbortController();
    request.current = abort;
    setSetup(loading);
    void (async () => {
      let partial: BucketSetup | null = null;
      let failed = false;
      try {
        for (const connection of await client.list(abort.signal)) {
          let result: BucketSetup | null = null;
          await offerBucketsAfterRefresh(
            connection,
            Promise.resolve(true),
            () => client.getBucketSuggestions(connection.id, abort.signal),
            () => !abort.signal.aborted,
            (offer) => {
              result = offer;
            },
          );
          if (abort.signal.aborted) return;
          const offer = result as BucketSetup | null;
          if (offer?.phase === "offered") {
            if (offer.proposal.assignments.length === 3) {
              setSetup(offer);
              return;
            }
            partial ??= offer;
          } else if (offer?.phase === "finished" && offer.error) failed = true;
        }
      } catch {
        failed = true;
      }
      if (!abort.signal.aborted) setSetup(partial ?? (failed ? unavailable : null));
    })();
    return () => abort.abort();
  }, [enabled, client, attempt]);

  const confirm = useCallback(() => {
    if (setup?.phase !== "offered") return;
    const proposal = setup.proposal;
    const current = request.current;
    setSetup({ phase: "saving", proposal });
    void saveBucketSuggestions(
      () => client.confirmBucketSuggestions(proposal),
      refreshPreferences,
      (result) => {
        if (current && !current.signal.aborted) setSetup(result);
      },
    );
  }, [setup, client, refreshPreferences]);

  const retry = useCallback(() => setAttempt((value) => value + 1), []);

  return { setup, confirm, retry };
}
