import { RequestError } from "../../http/RequestError.ts";
import type { UserImage } from "../../sessions/transcript/Messages.ts";
import { isUserImage } from "../ChatState.ts";

export const maxUserImageBytes = 10 * 1024 * 1024;

export function userImageUrl(sessionId: string, imageId: string): string {
  return `/api/sessions/${encodeURIComponent(sessionId)}/user-images/${encodeURIComponent(imageId)}`;
}

export async function uploadUserImage(
  sessionId: string,
  imageId: string,
  blob: Blob,
  signal: AbortSignal,
): Promise<UserImage> {
  if (!blob.size || blob.size > maxUserImageBytes)
    throw new TypeError("Image exceeds 10 MiB or is empty.");
  const response = await fetch(userImageUrl(sessionId, imageId), {
    method: "PUT",
    headers: {
      "X-Urd-Client": "browser",
      "Content-Type": blob.type.startsWith("image/") ? blob.type : "application/octet-stream",
      Accept: "application/json",
    },
    body: blob,
    cache: "no-store",
    credentials: "omit",
    redirect: "error",
    signal,
  });
  if (!response.ok) throw new RequestError(response.status, await response.text());
  const image: unknown = await response.json();
  if (!isUserImage(image) || image.imageId !== imageId)
    throw new TypeError("Invalid image upload acknowledgement.");
  return image;
}
