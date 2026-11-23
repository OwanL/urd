import type { RefObject } from "preact";
import { Icon } from "../../controls/Icon.tsx";
import type { UserImage } from "../../sessions/transcript/Messages.ts";
import { StoredUserImage } from "./StoredUserImage.tsx";
import type { ImageUploads } from "./useImageUploads.ts";
import "./draft-image-previews.css";

export function DraftImagePreviews({
  sessionId,
  images,
  uploads,
  input,
  onRemove,
  modelError,
  paused = false,
  readOnly = false,
}: {
  sessionId: string;
  images: readonly UserImage[];
  uploads: ImageUploads;
  input: RefObject<HTMLTextAreaElement | null>;
  onRemove: (id: string) => void;
  modelError: string | null;
  paused?: boolean;
  readOnly?: boolean;
}) {
  const restoreFocus = () => input.current?.focus({ preventScroll: true });
  const error = uploads.pasteError || uploads.images.find((image) => image.error)?.error;
  const status = error || (uploads.images.length ? "Uploading images…" : null);
  const cards = [
    ...images.map((image) => ({ id: image.imageId, image, pending: undefined })),
    ...uploads.images.map((pending) => ({ id: pending.id, pending, image: undefined })),
  ];
  if (!cards.length && !status) return null;
  return (
    <div class="draft-image-previews">
      {cards.length > 0 && (
        <ul class="draft-image-list" aria-label="Pasted images">
          {cards.map(({ id, pending, image }, index) => (
            <li class="draft-image-card" key={id}>
              {image && <StoredUserImage sessionId={sessionId} image={image} paused={paused} />}
              {pending && <img src={pending.url} alt="Pasted image" />}
              {!readOnly && (
                <button
                  type="button"
                  class="draft-image-remove"
                  aria-label={`Remove image ${index + 1}`}
                  onClick={() => {
                    restoreFocus();
                    if (pending) uploads.remove(id);
                    else {
                      onRemove(id);
                      uploads.clearPasteError();
                    }
                  }}
                >
                  <Icon name="close" />
                </button>
              )}
              {pending?.error && (
                <button
                  type="button"
                  class="draft-image-retry"
                  aria-label={`Retry image ${index + 1}`}
                  onClick={() => {
                    restoreFocus();
                    uploads.retry(id);
                  }}
                >
                  <Icon name="refresh" />
                </button>
              )}
            </li>
          ))}
        </ul>
      )}
      <div class="draft-image-feedback" role="status">
        {status}
        {modelError && <span>{modelError}</span>}
      </div>
    </div>
  );
}
