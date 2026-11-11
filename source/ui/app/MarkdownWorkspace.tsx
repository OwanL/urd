import { render } from "preact";
import { useEffect, useState } from "preact/hooks";
import { Workspace } from "./Workspace.tsx";
import { RichMessageBody } from "./RichMessageBody.tsx";
import type { TranscriptDisplay } from "./TranscriptDisplay.ts";
import { MarkdownClient } from "../markdown-rendering/MarkdownClient.ts";

// Normal startup and isolated previews share the same bounded application composition.
export function mountMarkdownWorkspace(
  root: HTMLElement,
  limits: Pick<TranscriptDisplay, "retainedPieces" | "retainedTabs">,
) {
  const releases = new Set<Promise<void>>();
  const errors: unknown[] = [];
  let reportError: ((message: string) => void) | null = null;
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
          if (errors.length === 0) errors.push(error);
          reportError?.(error instanceof Error ? error.message : "Transcript release failed.");
        },
      );
    },
  };
  function Application() {
    const [error, setError] = useState<string | null>(null);
    useEffect(() => {
      reportError = setError;
      return () => {
        reportError = null;
      };
    }, []);
    return <Workspace transcript={display} transcriptError={error} />;
  }
  render(<Application />, root);
  return {
    errors,
    async dispose() {
      render(null, root);
      while (releases.size) await Promise.allSettled([...releases]);
      if (errors.length) throw new AggregateError(errors, "Markdown view release failed.");
    },
  };
}
