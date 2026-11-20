import type { ToolCallTranscript } from "../../../sessions/transcript/Messages.ts";

export interface InspectionTarget {
  readonly kind: "inspection";
  readonly id: number;
  readonly anchor: HTMLElement;
  readonly requestId: string;
  readonly callId: string;
  readonly returned: boolean;
  readonly historyRevision: number;
  readonly payloadFinished: boolean;
  readonly label: string;
  readonly pinned: boolean;
}

export interface MenuTarget {
  readonly kind: "menu";
  readonly id: number;
  readonly anchor: HTMLElement;
  readonly path: string;
}

export type ActiveTarget = InspectionTarget | MenuTarget;
export interface PreviewIdentity {
  readonly id: number;
  readonly requestId: string;
  readonly callId: string;
  readonly historyRevision: number;
  readonly payloadFinished: boolean;
}

export type Preview =
  | (PreviewIdentity & { readonly state: "loading" })
  | (PreviewIdentity & { readonly state: "ready"; readonly transcript: ToolCallTranscript })
  | (PreviewIdentity & { readonly state: "missing" })
  | (PreviewIdentity & { readonly state: "error" });
export type CopyState = "idle" | "copying" | "copied" | "error";
