import { useState } from "preact/hooks";
import { SourceIgnorePicker } from "../setup/SourceIgnorePicker.tsx";

const sourceKinds = ["instructions", "skills"] as const;

export function SourceSettings({ active }: { active: boolean }) {
  const [expanded, setExpanded] = useState(false);
  const [kind, setKind] = useState<(typeof sourceKinds)[number]>("instructions");
  const [busy, setBusy] = useState(false);
  const showPicker = active && expanded;

  return (
    <details class="source-settings" onToggle={(event) => setExpanded(event.currentTarget.open)}>
      <summary>Instruction &amp; skill sources</summary>
      <div class="source-settings-body">
        <div class="source-settings-categories" role="group" aria-label="Source category">
          {sourceKinds.map((category) => (
            <button
              key={category}
              type="button"
              aria-pressed={kind === category}
              disabled={busy}
              onClick={() => setKind(category)}
            >
              {category === "instructions" ? "Instructions" : "Skills"}
            </button>
          ))}
        </div>
        {(showPicker || busy) && (
          <SourceIgnorePicker kind={kind} active={showPicker} onBusyChange={setBusy} />
        )}
      </div>
    </details>
  );
}
