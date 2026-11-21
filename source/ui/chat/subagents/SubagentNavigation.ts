import { createContext } from "preact";

export interface SubagentLink {
  readonly id: string;
  readonly parentId: string;
  readonly title: string;
}

export const SubagentNavigationContext = createContext<
  ((child: SubagentLink, opener: HTMLButtonElement) => void) | null
>(null);
