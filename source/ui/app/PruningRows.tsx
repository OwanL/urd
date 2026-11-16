import type { SessionPruningStatus } from "../sessions/SessionSnapshot.ts";

const categories = [
  { id: "tools", label: "Tools" },
  { id: "skills", label: "Skills" },
  { id: "instructions", label: "Instructions" },
] as const;

const stateLabels: Record<string, string> = {
  pending: "Pending",
  running: "Running",
  completed: "Completed",
  fallback: "Fallback",
  cancelled: "Cancelled",
  interrupted: "Interrupted",
};

export function PruningRows({
  pruning,
}: {
  pruning: readonly SessionPruningStatus[] | null | undefined;
}) {
  if (pruning === undefined || pruning === null) return null;

  const recorded = new Map(pruning.map((status) => [status.category, status]));
  return (
    <section class="chat-pruning" aria-label="Session preparation">
      {categories.map(({ id, label }) => {
        const status = recorded.get(id);
        const state = status ? (stateLabels[status.state] ?? "Unknown") : "Pending";
        const counts = status ? `${status.retained}/${status.total} retained` : null;
        const error = status?.error?.trim();
        const errorDetail = error ? (error.length > 120 ? `${error.slice(0, 119)}…` : error) : null;
        return (
          <div class="chat-pruning-row" key={id}>
            <span class="chat-pruning-category">{label}</span>
            <span>
              {state}
              {counts && ` · ${counts}`}
            </span>
            {errorDetail && <span class="chat-pruning-error">{errorDetail}</span>}
          </div>
        );
      })}
    </section>
  );
}
