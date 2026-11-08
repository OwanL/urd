// Ephemeral sign-in status; authorization URLs contain public OAuth parameters, not tokens.
export interface ProviderSignIn {
  readonly id: string;
  readonly connectionId: string;
  readonly status: ProviderSignInStatus;
  readonly authorizationUrl: string | null;
  readonly expiresAt: string;
  readonly error: string | null;
}

export type ProviderSignInStatus =
  | "pending"
  | "completing"
  | "completed"
  | "cancelled"
  | "expired"
  | "failed";
