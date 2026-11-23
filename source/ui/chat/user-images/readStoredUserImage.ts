export async function readStoredUserImage(url: URL, signal: AbortSignal): Promise<Blob> {
  const response = await fetch(url, {
    method: "GET",
    headers: { "X-Urd-Client": "browser", Accept: "image/*, application/octet-stream" },
    cache: "no-store",
    credentials: "omit",
    redirect: "error",
    signal,
  });
  const mediaType = response.headers.get("content-type")?.split(";")[0]?.trim().toLowerCase();
  if (
    !response.ok ||
    !(mediaType?.startsWith("image/") || mediaType === "application/octet-stream")
  ) {
    await response.body?.cancel();
    throw new TypeError("Unable to load image.");
  }
  return response.blob();
}
