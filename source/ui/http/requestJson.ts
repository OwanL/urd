import { RequestError } from "./RequestError.ts";

// Browser transport only: callers own cancellation of reads and explicit command retries.
export async function requestJson<T>(
  url: URL,
  method: "GET" | "POST",
  body?: unknown,
  signal?: AbortSignal,
): Promise<T> {
  const headers: Record<string, string> = {
    "X-Urd-Client": "browser",
    Accept: "application/json",
  };
  const options: RequestInit = {
    method,
    headers,
    cache: "no-store",
    credentials: "omit",
    redirect: "error",
  };
  if (method === "POST") {
    headers["Content-Type"] = "application/json";
    options.body = JSON.stringify(body);
  }
  if (signal !== undefined) options.signal = signal;

  const response = await fetch(url, options);
  if (!response.ok) throw new RequestError(response.status, await readErrorBody(response));
  const mediaType = response.headers.get("content-type")?.split(";")[0]?.trim().toLowerCase();
  if (mediaType !== "application/json") {
    await response.body?.cancel();
    throw new TypeError("Server returned a non-JSON response.");
  }
  // These wire types describe our server, not a general runtime schema validator.
  return (await response.json()) as T;
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
