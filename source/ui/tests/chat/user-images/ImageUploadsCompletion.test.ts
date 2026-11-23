import { strict as assert } from "node:assert";
import { test } from "node:test";
import { ImageUploadQueue } from "../../../chat/user-images/useImageUploads.ts";
import { imageLimitError } from "../../../chat/user-images/ImagePaste.ts";
import type { UserImage } from "../../../sessions/transcript/Messages.ts";

void test("completed uploads reserve capacity until the draft observes them", async () => {
  const previousFetch = globalThis.fetch;
  let complete!: (response: Response) => void;
  globalThis.fetch = () =>
    new Promise((resolve) => {
      complete = resolve;
    });
  try {
    let uploaded: UserImage | undefined;
    let state: { images: readonly { id: string }[]; pasteError: string | null } = {
      images: [],
      pasteError: null,
    };
    const queue = new ImageUploadQueue(
      "session",
      (image) => {
        uploaded = image;
        return true;
      },
      (next) => {
        state = next;
      },
    );
    const existing = Array.from({ length: 7 }, (_, i) => ({
      imageId: String(i),
      width: 1,
      height: 1,
    }));
    const blob = new Blob(["x"], { type: "image/png" });
    queue.paste([blob], existing);
    const id = state.images[0]!.id;
    complete(new Response(JSON.stringify({ imageId: id, width: 1, height: 1 })));
    await new Promise((resolve) => setTimeout(resolve, 0));
    assert.ok(uploaded);
    assert.equal(state.images.length, 0);
    queue.paste([blob], existing);
    assert.equal(state.images.length, 0);
    assert.equal(state.pasteError, imageLimitError);
    queue.observe([...existing, uploaded]);
    queue.paste([blob], existing);
    assert.equal(state.images.length, 1);
    queue.dispose();
  } finally {
    globalThis.fetch = previousFetch;
  }
});
