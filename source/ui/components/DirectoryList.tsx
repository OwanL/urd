import { collection } from "@zag-js/listbox";
import { normalizeProps, useMachine } from "@zag-js/preact";
import * as listbox from "@zag-js/listbox";
import type { RefObject } from "preact";
import { useId, useLayoutEffect, useMemo } from "preact/hooks";
import { scrollOptionIntoView } from "./Popover.tsx";
import type { DirectoryEntry } from "../files/DirectoryClient.ts";

export interface DirectoryListProps {
  directories: readonly DirectoryEntry[];
  selectedPath: string | null;
  disabled: boolean;
  emptyMessage?: string | undefined;
  listRef: RefObject<HTMLDivElement | null>;
  onSelect(path: string | null): void;
  onOpen(path: string): void;
}

export function DirectoryList({
  directories,
  selectedPath,
  disabled,
  emptyMessage,
  listRef,
  onSelect,
  onOpen,
}: DirectoryListProps) {
  const folders = useMemo(
    () =>
      collection({
        items: directories,
        itemToValue: (directory) => directory.path,
        itemToString: (directory) => directory.name,
      }),
    [directories],
  );
  const api = listbox.connect(
    useMachine(listbox.machine, {
      id: useId(),
      collection: folders,
      value: selectedPath ? [selectedPath] : [],
      disabled,
      selectionMode: "single",
      deselectable: false,
      selectOnHighlight: true,
      onValueChange: ({ value }) => onSelect(value[0] ?? null),
    }),
    normalizeProps,
  );

  useLayoutEffect(() => {
    const list = listRef.current;
    if (!list) return;
    const observer = new ResizeObserver(() => {
      scrollOptionIntoView(list.querySelector('[aria-selected="true"]'));
    });
    observer.observe(list);
    return () => observer.disconnect();
  }, [listRef]);

  return (
    <div {...api.getRootProps()} class="directory-list" aria-disabled={disabled || undefined}>
      <span {...api.getLabelProps()} class="directory-list-label">
        Folders
      </span>
      <div
        {...api.getContentProps()}
        ref={listRef}
        onKeyDownCapture={(event) => {
          if (event.key !== "Enter") return;
          event.preventDefault();
          event.stopPropagation();
          const path = api.highlightedValue;
          if (!disabled && path) onOpen(path);
        }}
      >
        {directories.length === 0 && emptyMessage && (
          <p class="directory-list-empty">{emptyMessage}</p>
        )}
        {directories.map((directory) => {
          const itemProps = { item: directory, highlightOnHover: false };
          return (
            <div
              {...api.getItemProps(itemProps)}
              key={directory.path}
              class="directory-list-row"
              onDblClick={() => {
                if (!disabled) onOpen(directory.path);
              }}
            >
              <FolderIcon />
              <span {...api.getItemTextProps(itemProps)} class="directory-list-name">
                {directory.name}
              </span>
            </div>
          );
        })}
      </div>
    </div>
  );
}

function FolderIcon() {
  return (
    <svg class="directory-list-folder-icon" aria-hidden="true" viewBox="0 0 16 16">
      <path d="M1.5 3.5h5l1.5 2h6.5v7h-13z" />
    </svg>
  );
}
