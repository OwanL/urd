export interface BrowserSignIn {
  id: string;
  origin: string;
  stage: "requested" | "opening" | "visible";
}

export type BrowserSignInAction = "open" | "continue" | "cancel";
