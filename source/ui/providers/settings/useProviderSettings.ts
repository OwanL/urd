import { useLayoutEffect, useMemo, useRef, useState } from "preact/hooks";
import type { ProviderClient } from "../ProviderClient.ts";
import { ProviderSettingsController } from "./ProviderSettingsController.ts";

// Parent keeps this hook mounted when the settings view is closed.
export function useProviderSettings(
  client: ProviderClient,
  active: boolean,
  onChanged: () => void,
  providersTab: boolean,
  onProviderReady: ((connectionId: string) => Promise<boolean>) | undefined,
  refreshPreferences: () => Promise<void>,
  offerProviderBuckets = true,
) {
  const callbacks = useRef({ onChanged, onProviderReady, refreshPreferences });
  callbacks.current = { onChanged, onProviderReady, refreshPreferences };
  const controller = useMemo(
    () =>
      new ProviderSettingsController(
        client,
        () => callbacks.current.onChanged(),
        (id) => callbacks.current.onProviderReady?.(id),
        () => callbacks.current.refreshPreferences(),
        offerProviderBuckets,
      ),
    [client, offerProviderBuckets],
  );
  const [, render] = useState(0);

  useLayoutEffect(() => controller.subscribe(() => render((version) => version + 1)), [controller]);
  useLayoutEffect(() => () => controller.dispose(), [controller]);
  useLayoutEffect(() => {
    const update = () =>
      controller.setActivity(active, document.visibilityState === "visible", providersTab);
    const focus = () => {
      if (active) void controller.read();
    };
    update();
    document.addEventListener("visibilitychange", update);
    window.addEventListener("focus", focus);
    return () => {
      document.removeEventListener("visibilitychange", update);
      window.removeEventListener("focus", focus);
      controller.setActivity(false, false);
    };
  }, [controller, active, providersTab]);

  return { ...controller.snapshot, active, actions: controller };
}

export type ProviderSettingsState = ReturnType<typeof useProviderSettings>;
