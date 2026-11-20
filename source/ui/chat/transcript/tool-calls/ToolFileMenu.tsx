import type { RefObject } from "preact";

export function ToolFileMenu({
  firstMenuItem,
  secondMenuItem,
  shownMenuMessage,
  onCopy,
  onReveal,
}: {
  firstMenuItem: RefObject<HTMLButtonElement | null>;
  secondMenuItem: RefObject<HTMLButtonElement | null>;
  shownMenuMessage: string | null;
  onCopy: () => void;
  onReveal: () => void;
}) {
  function moveMenuFocus(event: KeyboardEvent) {
    const items = [firstMenuItem.current, secondMenuItem.current].filter(
      (item): item is HTMLButtonElement => item !== null,
    );
    if (items.length === 0) return;
    const currentIndex = items.indexOf(document.activeElement as HTMLButtonElement);
    if (event.key === "ArrowDown" || event.key === "ArrowUp") {
      event.preventDefault();
      const offset = event.key === "ArrowDown" ? 1 : -1;
      items[(currentIndex + offset + items.length) % items.length]?.focus();
    } else if (event.key === "Home" || event.key === "End") {
      event.preventDefault();
      items[event.key === "Home" ? 0 : items.length - 1]?.focus();
    }
  }

  return (
    <div class="tool-file-menu-items" onKeyDown={moveMenuFocus}>
      <button
        ref={firstMenuItem}
        type="button"
        role="menuitem"
        class="tool-file-menu-item"
        onClick={onCopy}
      >
        Copy path
      </button>
      <button
        ref={secondMenuItem}
        type="button"
        role="menuitem"
        class="tool-file-menu-item"
        onClick={onReveal}
      >
        Reveal in Explorer
      </button>
      {shownMenuMessage && (
        <p class="tool-file-menu-error" role="status">
          {shownMenuMessage}
        </p>
      )}
    </div>
  );
}
