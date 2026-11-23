import { useEffect, useRef, useState } from "preact/hooks";
import type { UserImage } from "../../sessions/transcript/Messages.ts";
import { userImageUrl } from "./UserImageClient.ts";
import { readStoredUserImage } from "./readStoredUserImage.ts";
import "./stored-user-image.css";

export function StoredUserImage({
  sessionId,
  image,
  root = null,
  paused = false,
}: {
  sessionId: string;
  image: UserImage;
  root?: HTMLElement | null;
  paused?: boolean;
}) {
  const host = useRef<HTMLDivElement>(null);
  const [visible, setVisible] = useState(false);
  const [source, setSource] = useState<string | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    const element = host.current;
    if (!element) return;
    const observer = new IntersectionObserver(
      ([entry]) => setVisible(entry?.isIntersecting ?? false),
      { root },
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, [root]);

  useEffect(() => {
    if (paused || !visible) return;
    const controller = new AbortController();
    let objectUrl: string | null = null;
    setFailed(false);
    void readStoredUserImage(
      new URL(userImageUrl(sessionId, image.imageId), window.location.origin),
      controller.signal,
    )
      .then((blob) => {
        if (controller.signal.aborted) return;
        objectUrl = URL.createObjectURL(blob);
        setSource(objectUrl);
      })
      .catch(() => {
        if (!controller.signal.aborted) setFailed(true);
      });
    return () => {
      controller.abort();
      if (objectUrl) URL.revokeObjectURL(objectUrl);
      setSource(null);
    };
  }, [sessionId, image.imageId, paused, visible]);

  return (
    <div
      ref={host}
      class="stored-user-image"
      style={{
        "--user-image-width": `${image.width}px`,
        "--user-image-ratio": image.width / image.height,
      }}
    >
      {source && !failed ? (
        <img src={source} alt="Attached image" onError={() => setFailed(true)} />
      ) : null}
      {failed ? <span role="status">Image unavailable</span> : null}
    </div>
  );
}
