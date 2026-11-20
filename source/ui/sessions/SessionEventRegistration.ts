// Posts the open and watched sessions for one event stream registration.
export async function postSessionEventRegistration(
  origin: string,
  streamId: string,
  open: string[],
  watched: string[],
  signal: AbortSignal,
): Promise<void> {
  const response = await fetch(new URL(`/api/events/${encodeURIComponent(streamId)}`, origin), {
    method: "POST",
    headers: {
      "X-Urd-Client": "browser",
      "Content-Type": "application/json",
    },
    body: JSON.stringify({ open, watched }),
    cache: "no-store",
    credentials: "omit",
    redirect: "error",
    signal,
  });
  signal.throwIfAborted();
  if (response.status !== 204)
    throw new Error(`Session event registration failed (HTTP ${response.status}).`);
}
