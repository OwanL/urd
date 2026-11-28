import type { ComponentChildren } from "preact";
import { useId, useLayoutEffect, useRef, useState } from "preact/hooks";
import { IconButton } from "../../controls/IconButton.tsx";
import { Popover } from "../../controls/Popover.tsx";
import "./composer.css";

interface ComposerControlsProps {
  selections: ComponentChildren;
  selectionLayoutKey: string;
  actions: ComponentChildren;
  indicators: ComponentChildren;
  focusFallback(): void;
}

export function ComposerControls({
  selections: selectionControls,
  selectionLayoutKey,
  actions: actionControls,
  indicators,
  focusFallback,
}: ComposerControlsProps) {
  const controls = useRef<HTMLDivElement>(null);
  const selections = useRef<HTMLDivElement>(null);
  const actions = useRef<HTMLDivElement>(null);
  const accounting = useRef<HTMLDivElement>(null);
  const accountingWidth = useRef(0);
  const [inlineAccounting, setInlineAccounting] = useState(true);
  const overflowTrigger = useRef<HTMLButtonElement>(null);
  const overflowId = useId();
  const [overflowOpen, setOverflowOpen] = useState(false);

  useLayoutEffect(() => {
    const row = controls.current;
    const pinned = selections.current;
    const buttons = actions.current;
    if (!row || !pinned || !buttons) return;
    function layout() {
      if (!row!.clientWidth) return;
      if (inlineAccounting && accounting.current)
        accountingWidth.current = accounting.current.getBoundingClientRect().width;
      const flex = pinned!.style.flex;
      pinned!.style.flex = "none";
      const pinnedWidth = pinned!.getBoundingClientRect().width;
      pinned!.style.flex = flex;
      const gap = Number.parseFloat(getComputedStyle(row!).columnGap) || 0;
      const actionGap = Number.parseFloat(getComputedStyle(buttons!).columnGap) || 0;
      const more = overflowTrigger.current?.getBoundingClientRect().width ?? 0;
      const actionWidth = buttons!.getBoundingClientRect().width - (more ? more + actionGap : 0);
      const next =
        pinnedWidth + accountingWidth.current + actionWidth + gap * 2 <= row!.clientWidth;
      if (next === inlineAccounting) return;
      const focused = document.activeElement;
      const group = accounting.current;
      const menu = document.getElementById(overflowId);
      if (
        focused === overflowTrigger.current ||
        group?.contains(focused) ||
        menu?.contains(focused) ||
        [...(group?.querySelectorAll("[aria-controls]") ?? [])].some((control) =>
          document.getElementById(control.getAttribute("aria-controls")!)?.contains(focused),
        )
      )
        focusFallback();
      setOverflowOpen(false);
      setInlineAccounting(next);
    }
    const observer = new ResizeObserver(layout);
    for (const element of [row, pinned, buttons, inlineAccounting ? accounting.current : null])
      if (element) observer.observe(element);
    layout();
    return () => observer.disconnect();
  }, [inlineAccounting, overflowOpen, selectionLayoutKey, overflowId, focusFallback]);

  return (
    <div class="composer-controls" ref={controls}>
      <div class="composer-selections" ref={selections}>
        {selectionControls}
      </div>
      {inlineAccounting && (
        <div class="composer-accounting" ref={accounting}>
          {indicators}
        </div>
      )}
      <div class="composer-actions" ref={actions}>
        {!inlineAccounting && (
          <div class="composer-overflow">
            <IconButton
              icon="more"
              label="More options"
              hint="Session details"
              buttonRef={overflowTrigger}
              aria-expanded={overflowOpen}
              aria-controls={overflowOpen ? overflowId : undefined}
              aria-haspopup="dialog"
              onClick={() => setOverflowOpen((open) => !open)}
            />
            {overflowOpen && (
              <Popover
                id={overflowId}
                anchor={overflowTrigger}
                label="Session details"
                class="composer-overflow-popover"
                placement="above"
                onClose={() => setOverflowOpen(false)}
              >
                <div class="composer-accounting" ref={accounting}>
                  {indicators}
                </div>
              </Popover>
            )}
          </div>
        )}
        {actionControls}
      </div>
    </div>
  );
}
