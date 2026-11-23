export const minuteMs = 60_000;
export const maximumSnoozeMinutes = 365 * 24 * 60;

export function initialSnooze(now: Date): { from: number; minutes: number } {
  const from = Math.floor(now.getTime() / minuteMs) * minuteMs;
  const end = new Date(now);
  end.setDate(end.getDate() + 1);
  end.setHours(9, 0, 0, 0);
  return { from, minutes: (end.getTime() - from) / minuteMs };
}

export function returnTime(until: Date, now = new Date()): string {
  const tomorrow = new Date(now);
  tomorrow.setDate(tomorrow.getDate() + 1);
  const sameDay = (a: Date, b: Date) =>
    a.getFullYear() === b.getFullYear() &&
    a.getMonth() === b.getMonth() &&
    a.getDate() === b.getDate();
  const day = sameDay(until, now)
    ? "today"
    : sameDay(until, tomorrow)
      ? "tomorrow"
      : new Intl.DateTimeFormat(undefined, {
          weekday: "short",
          month: "short",
          day: "numeric",
          ...(until.getFullYear() !== now.getFullYear() ? { year: "numeric" } : {}),
        }).format(until);
  return `${day} at ${new Intl.DateTimeFormat(undefined, { hour: "numeric", minute: "2-digit" }).format(until)}`;
}
