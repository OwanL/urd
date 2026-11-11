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
  | "arrow-down"
  | "terminal"
  | "file-read"
  | "file-write"
  | "edit"
  | "list"
  | "cancel"
  | "tool"
  | "background"
  | "return";

const paths: Record<IconName, string> = {
  send: "m22 2-7 20-4-9-9-4 20-7ZM22 2 11 13",
  stop: "M6 6h12v12H6Z",
  plus: "M12 5v14M5 12h14",
  close: "m6 6 12 12M6 18 18 6",
  chevron: "m6 9 6 6 6-6",
  moon: "M20.9 13A9 9 0 0 1 11 3.1 9 9 0 1 0 20.9 13Z",
  settings:
    "M9.5 3 9 5 7.5 6 5.5 5.5 3 10.5 4.5 12 3 13.5 5.5 18.5 7.5 18 9 19 9.5 21h5l.5-2 1.5-1 2 .5 2.5-5-1.5-1.5 1.5-1.5-2.5-5-2 .5-1.5-1-.5-2ZM15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z",
  refresh: "M20 7v5h-5M4 17v-5h5M6.1 6.1A8 8 0 0 1 20 12M4 12a8 8 0 0 0 13.9 5.9",
  check: "m5 12 4 4L19 6",
  search: "M19 10a9 9 0 1 1-18 0 9 9 0 0 1 18 0Zm-2.5 6.5L22 22",
  "arrow-down": "M12 4v16m-7-7 7 7 7-7",
  terminal: "m4 17 6-6-6-6M12 19h8",
  "file-read": "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8ZM14 2v6h6M8 13h8M8 17h6",
  "file-write":
    "M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8ZM14 2v6h6M12 18v-6M9 15h6",
  edit: "M12 20h9M16.5 3.5a2.1 2.1 0 0 1 3 3L7 19l-4 1 1-4Z",
  list: "M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01",
  cancel: "M6 6h12v12H6ZM9 9l6 6m0-6-6 6",
  tool: "M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.8-3.8a6 6 0 0 1-7.9 7.9l-6.9 6.9a2.1 2.1 0 0 1-3-3l6.9-6.9a6 6 0 0 1 7.9-7.9Z",
  background: "M7 17 17 7M8 7h9v9",
  return: "M9 10 4 15l5 5M20 4v7a4 4 0 0 1-4 4H4",
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
