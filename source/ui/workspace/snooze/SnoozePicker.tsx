import type { RefObject } from "preact";
import { useRef, useState } from "preact/hooks";
import { Popover } from "../../controls/Popover.tsx";
import { initialSnooze, maximumSnoozeMinutes, minuteMs, returnTime } from "./SnoozeDuration.ts";
import { SnoozeWheel } from "./SnoozeWheel.tsx";
import "./snooze.css";

const units = [
  { label: "Days", step: 1440, cycle: 366 },
  { label: "Hours", step: 60, cycle: 24 },
  { label: "Minutes", step: 1, cycle: 60 },
];

export function SnoozePicker({
  anchor,
  title,
  available,
  snooze,
  onClose,
  onSnoozed,
}: {
  anchor: RefObject<HTMLElement | null>;
  title: string;
  available: boolean;
  snooze: (until: string) => Promise<string | null>;
  onClose: () => void;
  onSnoozed: () => void;
}) {
  const [{ from, minutes: initial }] = useState(() => initialSnooze(new Date()));
  const [minutes, setMinutes] = useState(initial);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const submitting = useRef(false);
  const submitButton = useRef<HTMLButtonElement>(null);
  const until = new Date(from + minutes * minuteMs);
  const problem =
    !available && !pending ? "Only idle chats with no pending work can be snoozed." : error;

  async function confirm() {
    if (submitting.current || !minutes || !available) return;
    if (until.getTime() <= Date.now()) {
      setError("Choose a return time in the future.");
      return;
    }
    submitting.current = true;
    setPending(true);
    setError(null);
    const failure = await snooze(until.toISOString());
    submitting.current = false;
    setPending(false);
    if (failure) setError(failure);
    else onSnoozed();
  }

  return (
    <Popover
      anchor={anchor}
      label={`Snooze ${title}`}
      class="snooze-picker"
      initialFocus={submitButton}
      onClose={onClose}
    >
      <div class="snooze-labels" aria-hidden="true">
        {units.map(({ label }) => (
          <span key={label}>{label}</span>
        ))}
      </div>
      <div class="snooze-wheels">
        {units.map((unit) => (
          <SnoozeWheel
            key={unit.label}
            {...unit}
            minutes={minutes}
            disabled={pending}
            adjust={(delta) => {
              setError(null);
              setMinutes((value) => Math.min(maximumSnoozeMinutes, Math.max(0, value + delta)));
            }}
          />
        ))}
      </div>
      <div class="snooze-footer">
        <output>
          {minutes ? (
            <>
              Returns <strong>{returnTime(until)}</strong>
            </>
          ) : (
            "Choose a duration"
          )}
        </output>
        <button
          type="button"
          class="snooze-confirm"
          ref={submitButton}
          disabled={!available || !minutes}
          aria-busy={pending}
          onClick={() => void confirm()}
        >
          {pending ? "Snoozing…" : "Snooze"}
        </button>
      </div>
      {problem && (
        <div class="snooze-error" role="alert">
          {problem}
        </div>
      )}
    </Popover>
  );
}
