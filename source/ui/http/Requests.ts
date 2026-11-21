import { RequestError } from "./RequestError.ts";

// Browser transport only: callers own cancellation of reads and explicit command retries.
export async function requestJson<T>(
  url: URL,
  method: "GET" | "POST" | "DELETE",
  body?: unknown,
  signal?: AbortSignal,
): Promise<T> {
  const response = await request(url, method, body, signal);
  const mediaType = response.headers.get("content-type")?.split(";")[0]?.trim().toLowerCase();
  if (mediaType !== "application/json") {
    await response.body?.cancel();
    throw new TypeError("Server returned a non-JSON response.");
  }
  // These wire types describe our server, not a general runtime schema validator.
  return (await response.json()) as T;
}

export async function requestBlob(
  url: URL,
  mediaTypes: readonly string[],
  signal: AbortSignal,
): Promise<Blob> {
  const response = await request(url, "GET", undefined, signal, mediaTypes.join(", "));
  const mediaType = response.headers.get("content-type")?.split(";")[0]?.trim().toLowerCase();
  if (!mediaType || !mediaTypes.includes(mediaType)) {
    await response.body?.cancel();
    throw new TypeError("Server returned an unsupported media type.");
  }
  return response.blob();
}

export async function requestEmpty(
  url: URL,
  method: "POST",
  body: unknown,
  signal?: AbortSignal,
): Promise<void> {
  const response = await request(url, method, body, signal);
  if (response.status === 204) return;
  await response.body?.cancel();
  throw new TypeError("Server did not return an empty acknowledgement (HTTP 204).");
}

async function request(
  url: URL,
  method: "GET" | "POST" | "DELETE",
  body?: unknown,
  signal?: AbortSignal,
  accept = "application/json",
): Promise<Response> {
  const headers: Record<string, string> = {
    "X-Urd-Client": "browser",
    Accept: accept,
  };
  const options: RequestInit = {
    method,
    headers,
    cache: "no-store",
    credentials: "omit",
    redirect: "error",
  };
  if (method !== "GET") {
    headers["Content-Type"] = "application/json";
    options.body = JSON.stringify(body);
  }
  if (signal !== undefined) options.signal = signal;

  const response = await fetch(url, options);
  if (!response.ok) throw new RequestError(response.status, await readErrorBody(response));
  return response;
}

async function readErrorBody(response: Response): Promise<unknown> {
  let text: string;
  try {
    text = await response.text();
  } catch {
    // Even a broken error body must not erase an already-received HTTP status.
    return undefined;
  }
  if (!text) return text;
  try {
    return JSON.parse(text) as unknown;
  } catch {
    return text;
  }
}
