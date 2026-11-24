// Viewer control glyphs the shared Icon set does not carry.
export type BrowserIconName = "browser" | "layout" | "minimize";

const paths: Record<BrowserIconName, string> = {
  browser:
    "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20ZM2 12h20" +
    "M12 2c2.9 2.9 4.3 6.2 4.3 10s-1.4 7.1-4.3 10c-2.9-2.9-4.3-6.2-4.3-10S9.1 4.9 12 2Z",
  layout: "M3 5h18v14H3ZM14 5v14",
  minimize: "M5 16h14",
};

export function BrowserIcon({ name }: { name: BrowserIconName }) {
  return (
    <svg
      class="icon"
      width="24"
      height="24"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      stroke-width="2"
      stroke-linecap="round"
      stroke-linejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      <path d={paths[name]} />
    </svg>
  );
}
