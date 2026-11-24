// Viewer control glyphs the shared Icon set does not carry.
export type BrowserIconName = "browser" | "dock" | "float" | "maximize" | "restore" | "minimize";

const paths: Record<BrowserIconName, string> = {
  browser:
    "M12 2a10 10 0 1 0 0 20 10 10 0 0 0 0-20ZM2 12h20" +
    "M12 2c2.9 2.9 4.3 6.2 4.3 10s-1.4 7.1-4.3 10c-2.9-2.9-4.3-6.2-4.3-10S9.1 4.9 12 2Z",
  dock: "M3 5h18v14H3ZM3 14h18",
  float: "M3 5h18v14H3ZM12 11h7v6h-7Z",
  maximize:
    "M8 3H5a2 2 0 0 0-2 2v3m11-5h3a2 2 0 0 1 2 2v3m0 8v3a2 2 0 0 1-2 2h-3m-8 0H5a2 2 0 0 1-2-2v-3",
  restore: "M8 8h12v12H8ZM4 16V4h12v4",
  minimize:
    "M8 3v3a2 2 0 0 1-2 2H3m18 0h-3a2 2 0 0 1-2-2V3m0 18v-3a2 2 0 0 1 2-2h3M3 16h3a2 2 0 0 1 2 2v3",
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
