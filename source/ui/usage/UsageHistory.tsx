import type { Chart } from "chart.js";
import { useEffect, useRef, useState } from "preact/hooks";
import type { CumulativeUsagePoint } from "./UsageValues.ts";
import { formatUsageCost, formatUsageCount, formatUsageTime } from "./UsageValues.ts";

type CostPoint = {
  x: number;
  y: number | null;
  at: string;
  totals: CumulativeUsagePoint["totals"];
};

export function UsageHistory({ history }: { history: CumulativeUsagePoint[] }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const chart = useRef<Chart<"line", CostPoint[]> | null>(null);
  const latest = useRef(history);
  const [unavailable, setUnavailable] = useState(false);
  latest.current = history;

  useEffect(() => {
    let disposed = false;
    // Load the chart library only when a visible cost popover needs it.
    void import("./UsageChart.ts")
      .then(({ Chart }) => {
        if (disposed || !canvas.current) return;
        const style = getComputedStyle(canvas.current);
        chart.current = new Chart<"line", CostPoint[]>(canvas.current, {
          type: "line",
          data: {
            datasets: [
              {
                data: points(latest.current),
                borderColor: style.getPropertyValue("--accent").trim(),
                borderWidth: 2,
                pointRadius: latest.current.length === 1 ? 3 : 0,
                pointHoverRadius: 3,
                pointHitRadius: 12,
                spanGaps: false,
              },
            ],
          },
          options: {
            responsive: true,
            maintainAspectRatio: false,
            animation: false,
            parsing: false,
            font: { family: style.fontFamily },
            interaction: { mode: "nearest", axis: "x", intersect: false },
            scales: {
              x: {
                type: "linear",
                grid: { display: false },
                border: { display: false },
                ticks: {
                  maxTicksLimit: 3,
                  maxRotation: 0,
                  color: style.getPropertyValue("--text-secondary").trim(),
                  font: { size: 11 },
                  callback: (value) => formatUsageTime(new Date(Number(value)).toISOString()),
                },
              },
              y: { display: false, beginAtZero: true },
            },
            plugins: {
              tooltip: {
                displayColors: false,
                callbacks: {
                  title: (items) =>
                    items[0] ? formatUsageTime((items[0].raw as CostPoint).at) : "",
                  label: (item) => {
                    const point = item.raw as CostPoint;
                    return [
                      formatUsageCost(point.totals),
                      `${formatUsageCount(point.totals.requests)} requests`,
                    ];
                  },
                },
              },
            },
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
  }, []);

  useEffect(() => {
    const current = chart.current;
    if (!current) return;
    current.data.datasets[0]!.data = points(history);
    current.data.datasets[0]!.pointRadius = history.length === 1 ? 3 : 0;
    current.update("none");
  }, [history]);

  return (
    <div class="usage-chart">
      {unavailable ? (
        <p class="usage-muted">Chart unavailable · values in breakdown</p>
      ) : (
        <canvas
          ref={canvas}
          role="img"
          aria-label={`Cumulative cost: ${formatUsageCost(history.at(-1)!.totals)}. Individual values are available under Breakdown and requests, History values.`}
        />
      )}
    </div>
  );
}

function points(history: CumulativeUsagePoint[]): CostPoint[] {
  return history.map(({ at, totals }) => ({
    x: new Date(at).getTime(),
    y:
      totals.costUsd === 0 && totals.requests > 0 && totals.unknownRequests === totals.requests
        ? null
        : totals.costUsd,
    at,
    totals,
  }));
}
