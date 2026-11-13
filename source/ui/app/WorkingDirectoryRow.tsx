import { useLayoutEffect, useRef, useState } from "preact/hooks";
import { FolderPicker } from "../components/FolderPicker.tsx";
import { Icon } from "../components/Icon.tsx";

export interface WorkingDirectoryRowProps {
  label: string;
  directory: string | null;
  disabled: boolean;
  busy: boolean;
  onSave(path: string): Promise<boolean>;
}

export function WorkingDirectoryRow({
  label,
  directory,
  disabled,
  busy,
  onSave,
}: WorkingDirectoryRowProps) {
  const row = useRef<HTMLDivElement>(null);
  const editButton = useRef<HTMLButtonElement>(null);
  const input = useRef<HTMLInputElement>(null);
  const browseButton = useRef<HTMLButtonElement>(null);
  const saveInProgress = useRef(false);
  const restoreEditFocus = useRef(false);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(directory ?? "");
  const [pickerOpen, setPickerOpen] = useState(false);
  const [saving, setSaving] = useState(false);

  useLayoutEffect(() => {
    if (editing) {
      input.current?.focus({ preventScroll: true });
      input.current?.select();
    } else if (restoreEditFocus.current) {
      restoreEditFocus.current = false;
      editButton.current?.focus({ preventScroll: true });
    }
  }, [editing]);

  const canMutate = !disabled && !busy;
  const closeEditor = () => {
    if (saveInProgress.current) return;
    restoreEditFocus.current = Boolean(row.current?.contains(document.activeElement));
    setPickerOpen(false);
    setEditing(false);
  };

  const save = async () => {
    if (!editing || !canMutate || saveInProgress.current || !draft.trim()) return;
    saveInProgress.current = true;
    setSaving(true);
    let succeeded = false;
    try {
      succeeded = await onSave(draft);
    } catch {
      succeeded = false;
    }
    saveInProgress.current = false;
    setSaving(false);
    if (!succeeded) return;

    restoreEditFocus.current = Boolean(row.current?.contains(document.activeElement));
    setPickerOpen(false);
    setEditing(false);
  };

  return (
    <div
      ref={row}
      class="chat-working-directory-row"
      onKeyDown={(event) => {
        if (
          event.key !== "Escape" ||
          event.isComposing ||
          !editing ||
          pickerOpen ||
          saveInProgress.current
        )
          return;
        event.preventDefault();
        closeEditor();
      }}
    >
      <span class="chat-working-directory-label">{label}</span>
      {editing ? (
        <div class="chat-working-directory-editor">
          <input
            ref={input}
            class="chat-working-directory-input"
            type="text"
            aria-label={label}
            autoComplete="off"
            spellcheck={false}
            value={draft}
            readOnly={!canMutate || saving}
            onInput={(event) => setDraft(event.currentTarget.value)}
            onKeyDown={(event) => {
              if (event.key !== "Enter" || event.isComposing || event.keyCode === 229) return;
              event.preventDefault();
              void save();
            }}
          />
          <div class="chat-working-directory-actions">
            <button
              ref={browseButton}
              class="chat-working-directory-control"
              type="button"
              aria-haspopup="dialog"
              aria-expanded={pickerOpen}
              aria-disabled={!canMutate || saving}
              onClick={() => {
                if (canMutate && !saveInProgress.current) setPickerOpen(true);
              }}
            >
              Browse…
            </button>
            <button
              class="chat-working-directory-control"
              type="button"
              aria-disabled={!canMutate || saving || !draft.trim()}
              onClick={() => void save()}
            >
              Save
            </button>
            <button
              class="chat-working-directory-control"
              type="button"
              aria-disabled={saving}
              onClick={closeEditor}
            >
              Cancel
            </button>
          </div>
        </div>
      ) : (
        <>
          <span class="chat-working-directory-path">{directory ?? "not set"}</span>
          <button
            ref={editButton}
            class="chat-working-directory-control chat-working-directory-edit"
            type="button"
            aria-label={`Edit ${label.toLowerCase()}`}
            aria-disabled={!canMutate}
            onClick={() => {
              if (!canMutate) return;
              setDraft(directory ?? "");
              setEditing(true);
            }}
          >
            <Icon name="edit" />
            <span>Edit</span>
          </button>
        </>
      )}
      {editing && pickerOpen && (
        <FolderPicker
          anchor={browseButton}
          initialPath={draft.trim() ? draft : null}
          onChoose={setDraft}
          onClose={(returnFocus) => {
            setPickerOpen(false);
            if (returnFocus) browseButton.current?.focus({ preventScroll: true });
          }}
        />
      )}
    </div>
  );
}
