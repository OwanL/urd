import { useEffect, useMemo, useState } from "preact/hooks";
import { McpClient } from "./McpClient.ts";
import { McpSettingsController } from "./McpSettingsController.ts";

// Owned by Settings so drafts and accepted operations survive closing the popover.
export function useMcpSettings(active: boolean) {
  const client = useMemo(() => new McpClient(new URL(window.location.href)), []);
  const controller = useMemo(() => new McpSettingsController(client), [client]);
  const [, render] = useState(0);

  useEffect(() => controller.subscribe(() => render((version) => version + 1)), [controller]);
  useEffect(
    () => () => {
      controller.dispose();
    },
    [controller],
  );
  useEffect(() => {
    controller.setActivity(active);
  }, [controller, active]);

  return { ...controller.snapshot, actions: controller };
}

export type McpSettingsState = ReturnType<typeof useMcpSettings>;
