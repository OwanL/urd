import type { SessionWorkingTime } from "../../sessions/SessionSnapshot.ts";

export function workingMilliseconds(time: SessionWorkingTime, now: number): number {
  const started = time.startedAt ? Date.parse(time.startedAt) : NaN;
  return time.completedMilliseconds + (Number.isFinite(started) ? Math.max(0, now - started) : 0);
}

export function formatWorkingTime(milliseconds: number): string {
  const seconds = Math.floor(Math.max(0, milliseconds) / 1_000);
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 60) return `${minutes}m ${seconds % 60}s`;
  return `${Math.floor(minutes / 60)}h ${minutes % 60}m`;
}
