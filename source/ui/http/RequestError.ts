// An HTTP failure, never a durable refusal or proof that an earlier command had no effect.
export class RequestError extends Error {
  readonly status: number;
  readonly responseBody: unknown;

  constructor(status: number, responseBody: unknown) {
    super(`Request failed (HTTP ${status}).`);
    this.name = "RequestError";
    this.status = status;
    this.responseBody = responseBody;
  }
}
