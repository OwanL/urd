import type { Chart } from "chart.js";
import { useEffect, useId, useMemo, useRef, useState } from "preact/hooks";
import { Popover } from "../controls/Popover.tsx";
import { UsagePointDetails } from "./UsageProviders.tsx";
import type { CumulativeUsagePoint } from "./UsageAggregation.ts";
import { usageProviders } from "./UsageAggregation.ts";
import { formatUsageCost, formatUsageTime, usageColors } from "./UsagePresentation.ts";

type CostPoint = { x: number; y: number | null };

export function UsageHistory({
  history,
  dateLabels = false,
  selected,
  onSelect,
}: {
  history: CumulativeUsagePoint[];
  dateLabels?: boolean;
  selected: number | null;
  onSelect: (index: number | null) => void;
}) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const detailAnchor = useRef<HTMLElement | null>(null);
  const chart = useRef<Chart<"line", CostPoint[]> | null>(null);
  const [unavailable, setUnavailable] = useState(false);
  const detailId = useId();
  const providers = useMemo(() => usageProviders(history.at(-1)?.breakdown ?? []), [history]);
  const colors = useMemo(() => usageColors(providers.map((provider) => provider.key)), [providers]);
  const point = selected === null ? undefined : history[selected];

  useEffect(() => {
    let disposed = false;
    detailAnchor.current = canvas.current?.closest<HTMLElement>(".usage-popover") ?? null;
    void import("./UsageChart.ts")
      .then(({ Chart }) => {
        if (disposed || !canvas.current || history.length === 0) return;
        const style = getComputedStyle(canvas.current);
        const rows = history.map(
          (sample) =>
            new Map(usageProviders(sample.breakdown).map((provider) => [provider.key, provider])),
        );
        const first = Date.parse(history[0]!.at);
        const last = Date.parse(history.at(-1)!.at);
        chart.current = new Chart<"line", CostPoint[]>(canvas.current, {
          type: "line",
          data: {
            datasets: providers.map((provider) => ({
              label: provider.label,
              data: history.map((sample, index) => {
                const totals = rows[index]!.get(provider.key)?.totals;
                return {
                  x: Date.parse(sample.at),
                  y:
                    totals &&
                    totals.costUsd === 0 &&
                    totals.requests > 0 &&
                    totals.unknownRequests === totals.requests
                      ? null
                      : (totals?.costUsd ?? 0),
                };
              }),
              borderColor: colors[provider.key],
              backgroundColor: `${colors[provider.key]}55`,
              borderWidth: 1.5,
              fill: "stack",
              pointRadius: history.length === 1 ? 3 : 0,
              pointHoverRadius: 3,
              spanGaps: false,
            })),
          },
          options: {
            responsive: true,
            maintainAspectRatio: false,
            animation: false,
            parsing: false,
            font: { family: style.fontFamily },
            interaction: { mode: "index", axis: "x", intersect: false },
            onHover: (_event, elements) => {
              if (elements[0]) onSelect(elements[0].index);
            },
            onClick: (_event, elements) => {
              if (elements[0]) onSelect(elements[0].index);
            },
            scales: {
              x: {
                type: "linear",
                min: first === last ? first - 30_000 : first,
                max: first === last ? last + 30_000 : last,
                grid: { display: false },
                border: { display: false },
                ticks: {
                  maxTicksLimit: 3,
                  maxRotation: 0,
                  color: style.getPropertyValue("--text-secondary").trim(),
                  font: { size: 10 },
                  callback: (value) =>
                    new Intl.DateTimeFormat(undefined, {
                      ...(dateLabels ? ({ month: "short", day: "numeric" } as const) : {}),
                      hour: "numeric",
                      minute: "2-digit",
                    }).format(new Date(Number(value))),
                },
              },
              y: { display: false, beginAtZero: true, stacked: true },
            },
            plugins: { tooltip: { enabled: false } },
          },
        });
      })
      .catch(() => {
        if (!disposed) setUnavailable(true);
      });
    return () => {
      disposed = true;
      chart.current?.destroy();
      chart.current = null;
    };
  }, [history, providers, colors, dateLabels, onSelect]);

  useEffect(() => {
    const current = chart.current;
    if (!current) return;
    current.setActiveElements(
      selected === null
        ? []
        : providers.map((_provider, datasetIndex) => ({ datasetIndex, index: selected })),
    );
    current.update("none");
  }, [selected, providers]);

  if (history.length === 0) return <p class="usage-muted">No usage yet</p>;
  const latest = history.at(-1)!;
  return (
    <div class="usage-chart">
      {unavailable ? (
        <p class="usage-muted">Chart unavailable</p>
      ) : (
        <canvas
          ref={canvas}
          role="slider"
          tabIndex={0}
          aria-label="Cumulative cost history"
          aria-valuemin={0}
          aria-valuemax={history.length - 1}
          aria-valuenow={selected ?? history.length - 1}
          aria-valuetext={`${formatUsageTime((point ?? latest).at)}, ${formatUsageCost((point ?? latest).totals)}`}
          aria-describedby={point ? detailId : undefined}
          onFocus={() => onSelect(history.length - 1)}
          onKeyDown={(event) => {
            let index = selected ?? history.length - 1;
            if (event.key === "ArrowLeft" || event.key === "ArrowDown") index--;
            else if (event.key === "ArrowRight" || event.key === "ArrowUp") index++;
            else if (event.key === "Home") index = 0;
            else if (event.key === "End") index = history.length - 1;
            else return;
            event.preventDefault();
            onSelect(Math.max(0, Math.min(history.length - 1, index)));
          }}
        />
      )}
      {point && (
        <Popover
          anchor={detailAnchor}
          id={detailId}
          class="usage-point-popover"
          role="tooltip"
          placement="below"
          autoFocus={false}
          onClose={() => onSelect(null)}
        >
          <UsagePointDetails point={point} colors={colors} />
        </Popover>
      )}
    </div>
  );
}
