import { RequestError } from "./RequestError.ts";

// One message from an unknown thrown value; callers with a distinct not-an-Error
// wording pass it explicitly so the difference stays visible at the call site.
export function errorMessage(cause: unknown, notAnError?: string): string {
  if (cause instanceof Error) return cause.message;
  return notAnError ?? String(cause);
}

// The server's error text from a rejected request, falling back to the request's own failure message.
export function requestErrorDescription(error: RequestError): string {
  const body = error.responseBody;
  if (typeof body === "object" && body !== null && "error" in body) {
    const message = body.error;
    if (typeof message === "string" && message.trim()) return message;
  }
  return error.message;
}
