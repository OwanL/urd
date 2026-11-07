// One bounded page of saved session identities; the continuation cursor is opaque.
export interface SessionIdPage {
  readonly sessionIds: readonly string[];
  readonly nextCursor: string | null;
}
