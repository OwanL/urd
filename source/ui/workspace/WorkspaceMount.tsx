import { render } from "preact";
import { useEffect, useState } from "preact/hooks";
import { Workspace } from "./Workspace.tsx";
import { RichMessageBody } from "../chat/transcript/RichMessageBody.tsx";
import { errorMessage } from "../http/ErrorMessages.ts";
import type { TranscriptDisplay } from "../chat/transcript/TranscriptDisplay.ts";
import { MarkdownClient } from "../markdown-rendering/MarkdownClient.ts";
import { SetupDialog } from "../setup/SetupDialog.tsx";
import type { SetupState } from "../setup/SetupDialog.tsx";

export function mountWorkspace(
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
          reportError?.(errorMessage(error, "Transcript release failed."));
        },
      );
    },
  };
  function Application() {
    const [error, setError] = useState<string | null>(null);
    const [setup, setSetup] = useState<SetupState>("checking");
    useEffect(() => {
      reportError = setError;
      return () => {
        reportError = null;
      };
    }, []);
    return (
      <>
        <Workspace transcript={display} transcriptError={error} setup={setup} />
        <SetupDialog state={setup} onStateChange={setSetup} />
      </>
    );
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
