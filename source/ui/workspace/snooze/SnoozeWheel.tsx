import { useLayoutEffect, useMemo, useRef, useState } from "preact/hooks";
import { maximumSnoozeMinutes } from "./SnoozeDuration.ts";

const rowHeight = 25;
const dragThreshold = 4;
const scrollBuffer = 60;

interface MouseDrag {
  pointerId: number;
  x: number;
  y: number;
  scrollTop: number;
  option: number | null;
  moved: boolean;
}

function firstRow(value: number, maximum: number) {
  return Math.max(0, Math.min(value - scrollBuffer, maximum - 2 * scrollBuffer));
}

function positionRow(element: HTMLDivElement, offset: number) {
  element.scrollTo({ top: offset * rowHeight, behavior: "instant" });
}

function optionAt(target: EventTarget | null) {
  if (!(target instanceof HTMLElement)) return null;
  const option = target.closest<HTMLElement>("[data-option]")?.dataset.option;
  return option === undefined ? null : Number(option);
}

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
  const value = Math.floor(minutes / step);
  const maximum = Math.floor(maximumSnoozeMinutes / step);
  const repeats = maximum >= cycle;
  const [start, setStart] = useState(() => (repeats ? firstRow(value, maximum) : 0));
  const end = repeats ? start + 2 * scrollBuffer : maximum;
  const wheel = useRef<HTMLDivElement>(null);
  const positionedStart = useRef<number | null>(null);
  const reported = useRef(value);
  const moving = useRef(false);
  const touching = useRef(false);
  const drag = useRef<MouseDrag | null>(null);
  const snapTimer = useRef(0);
  const options = useMemo(
    () =>
      Array.from({ length: end - start + 1 }, (_, index) =>
        String((start + index) % cycle).padStart(step === 1 ? 2 : 1, "0"),
      ),
    [start, end, step, cycle],
  );

  useLayoutEffect(() => {
    const element = wheel.current;
    if (!element) return;
    // Other columns can shift this one's absolute value while keeping its displayed unit.
    if (reported.current === value && positionedStart.current === start && !disabled) return;
    moving.current = false;
    cancelSnap();
    if (value < start || value > end) {
      positionedStart.current = null;
      setStart(firstRow(value, maximum));
      return;
    }
    const position = () => {
      positionRow(element, value - start);
      reported.current = value;
      positionedStart.current = start;
    };
    if (element.clientHeight) {
      position();
      return;
    }
    // The containing popover opens in its own layout effect, after the wheels mount.
    const frame = requestAnimationFrame(position);
    return () => cancelAnimationFrame(frame);
  }, [value, start, end, maximum, disabled]);

  useLayoutEffect(() => cancelSnap, []);

  function centeredRow(element: HTMLDivElement) {
    return Math.min(end, Math.max(start, start + Math.round(element.scrollTop / rowHeight)));
  }

  function publish(next: number) {
    if (disabled || next === reported.current) return;
    const delta = next - reported.current;
    reported.current = next;
    adjust(delta * step);
  }

  function select(element: HTMLDivElement, next: number, animate = false) {
    const target = Math.max(0, Math.min(maximum, next));
    const top = (target - start) * rowHeight;
    moving.current =
      animate &&
      Math.abs(element.scrollTop - top) > 1 &&
      !window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    if (moving.current) {
      element.scrollTo({ top, behavior: "smooth" });
      settleAfterMotion(element);
      return;
    }
    if (target >= start && target <= end) positionRow(element, target - start);
    publish(target);
  }

  function cancelSnap() {
    window.clearTimeout(snapTimer.current);
    snapTimer.current = 0;
  }

  function settle(element: HTMLDivElement) {
    snapTimer.current = 0;
    if (
      disabled ||
      !moving.current ||
      drag.current ||
      touching.current ||
      positionedStart.current !== start
    )
      return;
    const row = centeredRow(element);
    if (Math.abs(element.scrollTop - (row - start) * rowHeight) > 1) {
      select(element, row, true);
      if (moving.current) return;
    }
    moving.current = false;
    publish(row);
    // Repeat the hours and minutes across carries without rendering the whole year.
    if (
      repeats &&
      ((start > 0 && row - start < cycle / 2) || (end < maximum && end - row < cycle / 2))
    )
      setStart(firstRow(row, maximum));
  }

  function settleAfterMotion(element: HTMLDivElement) {
    window.clearTimeout(snapTimer.current);
    snapTimer.current = window.setTimeout(() => settle(element), 120);
  }

  function finishTouch(element: HTMLDivElement, remaining: number) {
    if (remaining) return;
    touching.current = false;
    if (!disabled) settleAfterMotion(element);
  }

  function finishDrag(element: HTMLDivElement, pointerId: number, click: boolean) {
    const gesture = drag.current;
    if (gesture?.pointerId !== pointerId) return;
    const next = gesture.moved ? centeredRow(element) : click ? gesture.option : null;
    drag.current = null;
    delete element.dataset.dragging;
    if (!disabled && next !== null) select(element, next, true);
  }

  return (
    <div
      ref={wheel}
      class="snooze-wheel"
      style={{ "--snooze-row-height": `${rowHeight}px` }}
      role="spinbutton"
      aria-label={label}
      aria-valuenow={value % cycle}
      aria-valuemin={0}
      aria-valuemax={cycle - 1}
      aria-disabled={disabled}
      tabIndex={0}
      onScroll={(event) => {
        const element = event.currentTarget;
        if (positionedStart.current !== start) return;
        if (moving.current) {
          publish(centeredRow(element));
          if (!drag.current && !touching.current) settleAfterMotion(element);
        } else if (Math.abs(element.scrollTop - (reported.current - start) * rowHeight) > 1) {
          positionRow(element, reported.current - start);
        }
      }}
      onWheel={(event) => {
        if (disabled || drag.current || event.ctrlKey || !event.deltaY) return;
        // Let small trackpad deltas accumulate before snapping to a row.
        moving.current = true;
        settleAfterMotion(event.currentTarget);
      }}
      onTouchStart={() => {
        cancelSnap();
        touching.current = moving.current = !disabled;
      }}
      onTouchEnd={(event) => finishTouch(event.currentTarget, event.touches.length)}
      onTouchCancel={(event) => finishTouch(event.currentTarget, event.touches.length)}
      onPointerDown={(event) => {
        if (disabled || event.pointerType !== "mouse" || event.button !== 0) return;
        cancelSnap();
        const element = event.currentTarget;
        drag.current = {
          pointerId: event.pointerId,
          x: event.clientX,
          y: event.clientY,
          scrollTop: element.scrollTop,
          option: optionAt(event.target),
          moved: false,
        };
        element.focus({ preventScroll: true });
        element.setPointerCapture(event.pointerId);
      }}
      onPointerMove={(event) => {
        const gesture = drag.current;
        if (disabled || gesture?.pointerId !== event.pointerId) return;
        const offset = gesture.y - event.clientY;
        if (
          !gesture.moved &&
          Math.max(Math.abs(offset), Math.abs(gesture.x - event.clientX)) < dragThreshold
        )
          return;
        gesture.moved = true;
        moving.current = true;
        event.currentTarget.dataset.dragging = "";
        event.currentTarget.scrollTop = gesture.scrollTop + offset;
      }}
      onPointerUp={(event) => {
        if (drag.current?.pointerId !== event.pointerId) return;
        const hit = document.elementFromPoint(event.clientX, event.clientY);
        finishDrag(event.currentTarget, event.pointerId, event.currentTarget.contains(hit));
      }}
      onLostPointerCapture={(event) => finishDrag(event.currentTarget, event.pointerId, false)}
      onClick={(event) => {
        // Captured mouse gestures are resolved on pointer-up; only taps use click.
        if (disabled || (event instanceof PointerEvent && event.pointerType === "mouse")) return;
        const option = optionAt(event.target);
        if (option !== null) select(event.currentTarget, option, true);
      }}
      onKeyDown={(event) => {
        if (disabled) return;
        const current = centeredRow(event.currentTarget);
        const cycleStart = current - (current % cycle);
        const keys: Record<string, number> = {
          ArrowUp: current + 1,
          ArrowDown: current - 1,
          PageUp: current + 10,
          PageDown: current - 10,
          Home: cycleStart,
          End: cycleStart + cycle - 1,
        };
        if (!Object.hasOwn(keys, event.key)) return;
        event.preventDefault();
        cancelSnap();
        select(event.currentTarget, keys[event.key]!);
      }}
    >
      {options.map((text, index) => (
        <span
          key={start + index}
          aria-hidden="true"
          data-option={start + index}
          data-selected={start + index === value}
        >
          {text}
        </span>
      ))}
    </div>
  );
}
