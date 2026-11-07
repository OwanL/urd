// An HTTP failure, never a durable refusal or proof that an earlier command had no effect.
export class SessionRequestError extends Error {
  readonly status: number;
  readonly responseBody: unknown;

  constructor(status: number, responseBody: unknown) {
    super(`Session request failed (HTTP ${status}).`);
    this.name = 'SessionRequestError';
    this.status = status;
    this.responseBody = responseBody;
  }
}
