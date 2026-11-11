import { render } from "preact";
import { Workspace } from "../app/Workspace.tsx";
import { RichMessageBody } from "../app/RichMessageBody.tsx";
import type { TranscriptDisplay } from "../app/TranscriptDisplay.ts";
import { MarkdownClient } from "../markdown-rendering/MarkdownClient.ts";
import "../base.css";

// Explicit qualification composition only; main.tsx never imports this entry point.
// The caller supplies experiment limits, not production defaults.
export function mountMarkdownWorkspace(
  root: HTMLElement,
  limits: Pick<TranscriptDisplay, "retainedPieces" | "retainedTabs">,
) {
  const releases = new Set<Promise<void>>();
  const errors: unknown[] = [];
  const display: TranscriptDisplay = {
    ...limits,
    MessageBody: RichMessageBody,
    client: new MarkdownClient(new URL(location.href)),
    release(work) {
      releases.add(work);
      void work.then(
        () => releases.delete(work),
        (error: unknown) => {
          releases.delete(work);
          errors.push(error);
        },
      );
    },
  };
  render(<Workspace transcript={display} />, root);
  return {
    errors,
    async dispose() {
      render(null, root);
      while (releases.size) await Promise.allSettled([...releases]);
      if (errors.length) throw new AggregateError(errors, "Markdown view release failed.");
    },
  };
}
