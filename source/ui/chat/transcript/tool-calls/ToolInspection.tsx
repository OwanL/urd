import { formatToolInput } from "./ToolCallInspection.ts";
import { formatTokenCount } from "../../TokenCountFormatting.ts";
import type { CopyState, InspectionTarget, Preview } from "./ToolRowInteractionTypes.ts";

export function ToolInspection({
  active,
  shownPreview,
  copyState,
  onPointerEnter,
  onPointerLeave,
  onCopy,
}: {
  active: InspectionTarget;
  shownPreview: Preview | null;
  copyState: CopyState;
  onPointerEnter: () => void;
  onPointerLeave: () => void;
  onCopy: () => void;
}) {
  return (
    <div class="tool-inspection" onPointerEnter={onPointerEnter} onPointerLeave={onPointerLeave}>
      <div class="tool-inspection-header">
        <span>{active.label}</span>
        <button
          type="button"
          class="tool-overlay-action"
          aria-label="Copy full tool input and output"
          aria-disabled={copyState === "copying"}
          onClick={onCopy}
        >
          {copyState === "copying"
            ? "Copying…"
            : copyState === "copied"
              ? "Copied"
              : copyState === "error"
                ? "Copy failed"
                : "Copy"}
        </button>
      </div>
      {shownPreview?.state === "missing" && (
        <p class="tool-inspection-message">Tool call is no longer available.</p>
      )}
      {shownPreview?.state === "error" && (
        <p class="tool-inspection-message" role="status">
          Could not load tool call.
        </p>
      )}
      <div class="tool-inspection-section">
        <h3>
          {active.returned ? "Original call input" : "Input"}
          <TokenCount tokens={previewTokens(shownPreview, "input")} />
        </h3>
        <pre>{previewText(shownPreview, "input")}</pre>
        {active.returned && (
          <p class="tool-inspection-note">Shown for reference; not resent to the model.</p>
        )}
      </div>
      <div class="tool-inspection-section">
        <h3>
          Output
          <TokenCount tokens={previewTokens(shownPreview, "output")} />
        </h3>
        <pre>{previewText(shownPreview, "output")}</pre>
      </div>
      {shownPreview?.state === "ready" && shownPreview.transcript.truncated && (
        <p class="tool-inspection-note">Preview truncated. Copy fetches the full payload.</p>
      )}
      <span class="tool-inspection-feedback" aria-live="polite">
        {copyState === "error" ? "Could not copy tool call." : ""}
      </span>
    </div>
  );
}

function previewText(preview: Preview | null, part: "input" | "output"): string {
  if (!preview || preview.state === "loading") return "Loading…";
  if (preview.state !== "ready") return "—";
  if (part === "input") return formatToolInput(preview.transcript.input);
  return preview.transcript.output ?? "Running…";
}

function previewTokens(preview: Preview | null, part: "input" | "output"): number | null {
  if (preview?.state !== "ready") return null;
  return part === "input" ? preview.transcript.inputTokens : preview.transcript.outputTokens;
}

function TokenCount({ tokens }: { tokens: number | null }) {
  if (tokens === null) return null;
  return <span class="tool-inspection-tokens">~{formatTokenCount(tokens)} tokens</span>;
}
