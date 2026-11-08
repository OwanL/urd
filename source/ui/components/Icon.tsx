export type IconName =
  | "send"
  | "stop"
  | "plus"
  | "close"
  | "chevron"
  | "moon"
  | "settings"
  | "refresh"
  | "check"
  | "search"
  | "arrow-down";

const paths: Record<IconName, string> = {
  send: "m22 2-7 20-4-9-9-4 20-7ZM22 2 11 13",
  stop: "M6 6h12v12H6Z",
  plus: "M12 5v14M5 12h14",
  close: "m6 6 12 12M6 18 18 6",
  chevron: "m6 9 6 6 6-6",
  moon: "M20.9 13A9 9 0 0 1 11 3.1 9 9 0 1 0 20.9 13Z",
  settings:
    "m9 3-.5 2-2 1-2-.5-3 5L3 12l-1.5 1.5 3 5 2-.5 2 1L9 21h6l.5-2 2-1 2 .5 3-5L21 12l1.5-1.5-3-5-2 .5-2-1L15 3ZM15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z",
  refresh: "M20 7v5h-5M4 17v-5h5M6.1 6.1A8 8 0 0 1 20 12M4 12a8 8 0 0 0 13.9 5.9",
  check: "m5 12 4 4L19 6",
  search: "M19 10a9 9 0 1 1-18 0 9 9 0 0 1 18 0Zm-2.5 6.5L22 22",
  "arrow-down": "M12 4v16m-7-7 7 7 7-7",
};

export function Icon({ name }: { name: IconName }) {
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
