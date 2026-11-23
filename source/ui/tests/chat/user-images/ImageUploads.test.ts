import { strict as assert } from "node:assert";
import { test } from "node:test";
import { ImageUploadQueue } from "../../../chat/user-images/useImageUploads.ts";
import { imageLimitError } from "../../../chat/user-images/ImagePaste.ts";
import type { UserImage } from "../../../sessions/transcript/Messages.ts";

const image = (imageId: string): UserImage => ({ imageId, width: 1, height: 1 });
const blob = new Blob(["image"], { type: "image/png" });

void test("paste counts existing and pending images across pastes and recovers after removal", () => {
  const fetchBefore = globalThis.fetch;
  globalThis.fetch = () => new Promise(() => {});
  try {
    let state: { images: readonly { id: string }[]; pasteError: string | null } = {
      images: [],
      pasteError: null,
    };
    const queue = new ImageUploadQueue(
      "session",
      () => true,
      (next) => {
        state = next;
      },
    );
    const existing = Array.from({ length: 6 }, (_, i) => image(String(i)));
    queue.paste([blob, blob, blob], existing);
    assert.equal(state.images.length, 2);
    assert.equal(state.pasteError, imageLimitError);
    queue.paste([blob], existing);
    assert.equal(state.images.length, 2);
    queue.remove(state.images[1]!.id);
    assert.equal(state.pasteError, null);
    queue.paste([blob], existing);
    assert.equal(state.images.length, 2);
    assert.equal(state.pasteError, null);
    queue.dispose();
  } finally {
    globalThis.fetch = fetchBefore;
  }
});

void test("invalid blobs do not consume capacity", () => {
  const fetchBefore = globalThis.fetch;
  globalThis.fetch = () => new Promise(() => {});
  try {
    let count = 0;
    let error: string | null = null;
    const queue = new ImageUploadQueue(
      "session",
      () => true,
      (state) => {
        count = state.images.length;
        error = state.pasteError;
      },
    );
    queue.paste(
      [new Blob(), blob],
      Array.from({ length: 7 }, (_, i) => image(String(i))),
    );
    assert.equal(count, 1);
    assert.match(error!, /nonempty/);
    queue.dispose();
  } finally {
    globalThis.fetch = fetchBefore;
  }
});
