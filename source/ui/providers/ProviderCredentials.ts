// Presence and account metadata only; credentials remain in the backend.
export interface ProviderCredentialStatus {
  readonly connectionId: string;
  readonly hasCredentials: boolean;
  readonly requiresSignIn: boolean;
  readonly accountLabel: string | null;
  readonly expiresAt: string | null;
  readonly planAccessGranted: boolean | null;
  readonly isDisconnecting: boolean;
}

export interface ProviderDisconnectResult {
  readonly connectionId: string;
  readonly localCredentialsCleared: boolean;
  readonly remoteRevocationConfirmed: boolean | null;
}
