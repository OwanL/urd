import { useRef } from "preact/hooks";
import { maximumSnoozeMinutes } from "./SnoozeDuration.ts";

const offsets = [-2, -1, 0, 1, 2];

export function SnoozeWheel({
  label,
  step,
  cycle,
  minutes,
  adjust,
  disabled,
}: {
  label: string;
  step: number;
  cycle: number;
  minutes: number;
  adjust: (minutes: number) => void;
  disabled: boolean;
}) {
  const wheelMotion = useRef({ delta: 0, last: 0 });
  const value = (total: number) => Math.floor(total / step) % cycle;
  return (
    <div
      class="snooze-wheel"
      role="spinbutton"
      aria-label={label}
      aria-valuenow={value(minutes)}
      aria-valuemin={0}
      aria-valuemax={cycle - 1}
      aria-disabled={disabled}
      tabIndex={0}
      onWheel={(event) => {
        event.preventDefault();
        if (disabled || !event.deltaY) return;
        const motion = wheelMotion.current;
        if (event.timeStamp - motion.last > 180) motion.delta = 0;
        motion.last = event.timeStamp;
        const delta = event.deltaMode === 0 ? event.deltaY / 30 : event.deltaY;
        motion.delta += delta;
        const rows = Math.trunc(motion.delta);
        motion.delta -= rows;
        if (rows) adjust(rows * step);
      }}
      onKeyDown={(event) => {
        if (disabled) return;
        const rows =
          event.key === "ArrowUp"
            ? 1
            : event.key === "ArrowDown"
              ? -1
              : event.key === "PageUp"
                ? 10
                : event.key === "PageDown"
                  ? -10
                  : 0;
        if (!rows) return;
        event.preventDefault();
        adjust(rows * step);
      }}
    >
      {offsets.map((offset) => {
        const total = minutes + offset * step;
        const available = total >= 0 && total <= maximumSnoozeMinutes;
        return (
          <span
            key={offset}
            aria-hidden="true"
            class={available ? undefined : "is-empty"}
            onClick={available && offset && !disabled ? () => adjust(offset * step) : undefined}
          >
            {available ? String(value(total)).padStart(step === 1 ? 2 : 1, "0") : ""}
          </span>
        );
      })}
    </div>
  );
}
