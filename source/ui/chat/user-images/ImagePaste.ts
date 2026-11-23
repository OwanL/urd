export function pastedImages(data: DataTransfer | null): File[] {
  if (!data) return [];
  const files = Array.from(data.files).filter((file) => file.type.startsWith("image/"));
  if (files.length) return files;
  return Array.from(data.items).flatMap((item) => {
    if (item.kind !== "file" || !item.type.startsWith("image/")) return [];
    const file = item.getAsFile();
    return file ? [file] : [];
  });
}

export function pasteTarget(path: readonly EventTarget[], composer: HTMLTextAreaElement | null) {
  for (const target of path) {
    if (!(target instanceof Element)) continue;
    if (target === composer) return "composer";
    if (target instanceof HTMLTextAreaElement && target.closest("[data-message-editor]"))
      return target;
    if (
      target.matches(
        "input, textarea, select, [contenteditable], [role='textbox'], [role='combobox']",
      ) ||
      (target instanceof HTMLElement && target.isContentEditable)
    )
      return "other";
  }
  return "composer";
}

export const maxUserImages = 8;
export const imageLimitError =
  "A message can have at most 8 images. Remove an image to add another.";
export const imageModelError = "The selected model does not support images.";
