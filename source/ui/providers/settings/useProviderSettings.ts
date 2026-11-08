import { useLayoutEffect, useMemo, useRef, useState } from "preact/hooks";
import type { ProviderClient } from "../ProviderClient.ts";
import { ProviderSettingsController } from "./ProviderSettingsController.ts";

// Parent keeps this hook mounted when the settings view is closed.
export function useProviderSettings(
  client: ProviderClient,
  active: boolean,
  onChanged: () => void,
) {
  const onChangedRef = useRef(onChanged);
  onChangedRef.current = onChanged;
  const controller = useMemo(
    () => new ProviderSettingsController(client, () => onChangedRef.current()),
    [client],
  );
  const [, render] = useState(0);

  useLayoutEffect(() => controller.subscribe(() => render((version) => version + 1)), [controller]);
  useLayoutEffect(() => {
    const update = () => controller.setActivity(active, document.visibilityState === "visible");
    update();
    document.addEventListener("visibilitychange", update);
    return () => {
      document.removeEventListener("visibilitychange", update);
      controller.setActivity(false, false);
    };
  }, [controller, active]);

  return { ...controller.snapshot, active, actions: controller };
}

export type ProviderSettingsState = ReturnType<typeof useProviderSettings>;
