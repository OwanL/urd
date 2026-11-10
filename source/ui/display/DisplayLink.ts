// Destinations are already decoded by the parser. Do not HTML-decode again.
// Recheck at the DOM boundary even when the producer filtered destinations.
export function safeDisplayLink(destination: string | undefined): string | undefined {
  if (
    destination === undefined ||
    /[\s\u0000-\u001f\u007f-\u009f\\]/u.test(destination) ||
    destination.startsWith("//")
  )
    return undefined;
  const colon = destination.indexOf(":");
  const separator = destination.search(/[/?#]/u);
  if (colon < 0 || (separator >= 0 && separator < colon)) return destination;
  try {
    const url = new URL(destination);
    if ((url.protocol === "http:" || url.protocol === "https:") && url.hostname) return destination;
    if (url.protocol === "mailto:" && destination.length > "mailto:".length) return destination;
  } catch {
    // Malformed absolute URLs are labels, not navigation.
  }
  return undefined;
}
