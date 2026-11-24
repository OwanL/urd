// Match the content area's shape, but show a desktop layout even in a small window.
// The host accepts 240..3840 per axis; beyond a 16:1 ratio the frame is contained.
const min = 240;
const max = 3840;
const desktopWidth = 1280;

export function viewerViewport(width: number, height: number) {
  if (!Number.isFinite(width) || !Number.isFinite(height) || width <= 0 || height <= 0) return null;
  const ratio = Math.max(min / max, Math.min(max / min, width / height));
  const w = Math.min(max, max * ratio, Math.max(desktopWidth, width, min * ratio));
  return { width: Math.round(w), height: Math.round(w / ratio) };
}
