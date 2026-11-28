import { useLayoutEffect, useMemo, useRef, useState } from "preact/hooks";
import type { UserImage } from "../../sessions/transcript/Messages.ts";
import { RequestError } from "../../http/RequestError.ts";
import { newUuid } from "../../Uuid.ts";
import { maxUserImageBytes, uploadUserImage } from "./UserImageClient.ts";
import { maxUserImages, imageLimitError } from "./ImagePaste.ts";

interface PendingImage {
  id: string;
  blob: Blob;
  url: string;
  error?: string;
}

export interface ImageUploads {
  images: readonly PendingImage[];
  pasteError: string | null;
  pending(): boolean;
  paste(blobs: readonly Blob[], existing: readonly UserImage[]): void;
  remove(id: string): void;
  clearPasteError(): void;
  retry(id: string): void;
}

// The queue is tied to a composer or exact edit target, not to a transcript row.
const noImages: readonly PendingImage[] = [];

export function useImageUploads<T>(
  sessionId: string,
  scope: T,
  existing: readonly UserImage[],
  onUploaded: (image: UserImage, scope: T) => boolean,
): ImageUploads {
  const publish = useRef(onUploaded);
  publish.current = onUploaded;
  const [state, setState] = useState<{
    queue: ImageUploadQueue;
    images: readonly PendingImage[];
    pasteError: string | null;
  }>();
  const queue = useMemo(
    () => new ImageUploadQueue(sessionId, (image) => publish.current(image, scope), setState),
    // A new scope must never inherit the previous draft's uploads.
    [sessionId, scope],
  );
  queue.observe(existing);
  useLayoutEffect(() => () => queue.dispose(), [queue]);
  // One stable uploads object per queue state keeps command callbacks and context
  // values that capture uploads referentially stable across unrelated renders.
  return useMemo(
    () => ({
      images: state?.queue === queue ? state.images : noImages,
      pasteError: state?.queue === queue ? state.pasteError : null,
      pending: () => queue.pending(),
      paste: (blobs, existing) => queue.paste(blobs, existing),
      remove: (id) => queue.remove(id),
      clearPasteError: () => queue.clearPasteError(),
      retry: (id) => queue.retry(id),
    }),
    [queue, state],
  );
}

export class ImageUploadQueue {
  private images: PendingImage[] = [];
  private pasteError: string | null = null;
  private readonly unobserved = new Set<string>();
  private request: AbortController | null = null;

  private readonly sessionId: string;
  private readonly commit: (image: UserImage) => boolean;
  private readonly publish: (state: {
    queue: ImageUploadQueue;
    images: readonly PendingImage[];
    pasteError: string | null;
  }) => void;

  constructor(
    sessionId: string,
    commit: ImageUploadQueue["commit"],
    publish: ImageUploadQueue["publish"],
  ) {
    this.sessionId = sessionId;
    this.commit = commit;
    this.publish = publish;
  }

  pending() {
    return this.images.length !== 0;
  }

  observe(existing: readonly UserImage[]) {
    for (const image of existing) this.unobserved.delete(image.imageId);
  }

  paste(blobs: readonly Blob[], existing: readonly UserImage[]) {
    this.pasteError = null;
    this.observe(existing);
    let count = existing.length + this.unobserved.size + this.images.length;
    for (const blob of blobs) {
      if (blob.size > maxUserImageBytes || !blob.size) {
        this.pasteError = "Images must be nonempty and at most 10 MiB.";
        continue;
      }
      if (count >= maxUserImages) {
        this.pasteError = imageLimitError;
        continue;
      }
      this.images.push({ id: newUuid(), blob, url: URL.createObjectURL(blob) });
      count++;
    }
    this.update();
  }

  remove(id: string) {
    const index = this.images.findIndex((image) => image.id === id);
    if (index < 0) return;
    if (index === 0) {
      this.request?.abort();
      this.request = null;
    }
    this.pasteError = null;
    const [removed] = this.images.splice(index, 1);
    URL.revokeObjectURL(removed!.url);
    this.update();
  }

  clearPasteError() {
    if (!this.pasteError) return;
    this.pasteError = null;
    this.update();
  }

  retry(id: string) {
    const image = this.images[0];
    if (image?.id !== id || !image.error) return;
    this.images[0] = { id, blob: image.blob, url: image.url };
    this.update();
  }

  dispose() {
    this.request?.abort();
    this.request = null;
    for (const image of this.images) URL.revokeObjectURL(image.url);
    this.images = [];
  }

  private update() {
    this.publish({ queue: this, images: [...this.images], pasteError: this.pasteError });
    const image = this.images[0];
    if (image && !image.error && !this.request) void this.upload(image);
  }

  private async upload(image: PendingImage) {
    const request = new AbortController();
    this.request = request;
    try {
      const stored = await uploadUserImage(this.sessionId, image.id, image.blob, request.signal);
      if (request.signal.aborted) return;
      this.unobserved.add(stored.imageId);
      if (!this.commit(stored)) {
        this.unobserved.delete(stored.imageId);
        throw new Error("Image could not be added to the draft.");
      }
      this.images.shift();
      URL.revokeObjectURL(image.url);
    } catch (error) {
      this.unobserved.delete(image.id);
      if (request.signal.aborted) return;
      this.images[0] = {
        ...image,
        error:
          error instanceof RequestError
            ? `Image upload failed (HTTP ${error.status}).`
            : "Image upload failed.",
      };
    } finally {
      if (this.request === request) {
        this.request = null;
        this.update();
      }
    }
  }
}
