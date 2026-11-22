export function authorizationLink(value: string | null): string | undefined {
  if (!value) return undefined;
  try {
    const url = new URL(value);
    return url.protocol === "https:" && !url.username && !url.password ? url.href : undefined;
  } catch {
    return undefined;
  }
}

export function reserveSignInTab(): Window | null {
  if (typeof window === "undefined") return null;
  const tab = window.open("about:blank", "_blank");
  if (tab) tab.opener = null;
  return tab;
}
