// LAN hosting acknowledgement reconciliation, kept pure so Node tests can hold
// the sequence rules without a DOM.

// LAN hosting state as reported by /api/hosting/lan.
export interface HostingStatus {
  enabled: boolean;
  remembered: boolean;
  origins: string[];
  startupError: string | null;
}

// What the setting displays: the last reconciled state and its error.
export interface HostingDisplay {
  readonly status: HostingStatus | null;
  readonly error: string | null;
}

// One token per read or command; a later one supersedes earlier outcomes.
export type Acknowledgement = number;

// True when an outcome from this token may still be applied.
export function acknowledged(observed: Acknowledgement, current: Acknowledgement): boolean {
  return observed === current;
}

// A background read may additionally be applied only while no command is in
// flight; a superseded or held read is discarded rather than overwriting state.
export function readApplies(
  observed: Acknowledgement,
  current: Acknowledgement,
  busy: boolean,
): boolean {
  return acknowledged(observed, current) && !busy;
}

// Applies a completed read. While a command's acknowledgement is lost, the read
// reconciles status but must keep that error until the next command starts.
export function observeRead(read: HostingDisplay, heldError: string | null): HostingDisplay {
  return heldError === null ? read : { ...read, error: heldError };
}

// A lost command acknowledgement proves nothing about the command's effect:
// show the state as unknown and remember the failure until the next command.
export function lostAcknowledgement(error: string): HostingDisplay {
  return { status: null, error };
}
