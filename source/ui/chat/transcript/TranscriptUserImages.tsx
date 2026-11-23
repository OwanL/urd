import type { TranscriptEntryDescriptor, UserImage } from "../../sessions/transcript/Messages.ts";
import { isTextEntry } from "../../sessions/transcript/Messages.ts";
import { StoredUserImage } from "../user-images/StoredUserImage.tsx";

export function transcriptMessageContent(descriptor: TranscriptEntryDescriptor) {
  const images = descriptor.entry.kind === "user" ? (descriptor.images ?? []) : [];
  const text = isTextEntry(descriptor.entry) ? descriptor.text : null;
  const imageOnly =
    images.length > 0 &&
    text?.isClosed &&
    text.from.segmentIndex === text.availableThrough.segmentIndex &&
    text.from.utf8Offset === text.availableThrough.utf8Offset;
  return { text: imageOnly ? null : text, images };
}

export function TranscriptUserImages({
  sessionId,
  images,
  root,
  paused,
}: {
  sessionId: string;
  images: readonly UserImage[];
  root: HTMLElement | null;
  paused: boolean;
}) {
  if (!images.length) return null;
  return (
    <div class="message message-user transcript-user-images">
      {images.map((image) => (
        <StoredUserImage
          key={image.imageId}
          sessionId={sessionId}
          image={image}
          root={root}
          paused={paused}
        />
      ))}
    </div>
  );
}
