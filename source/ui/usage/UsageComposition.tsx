import type { Chart } from "chart.js";
import { useEffect, useRef, useState } from "preact/hooks";
import type { UsageItem } from "./UsageAggregation.ts";
import { formatUsageCost, usageColors } from "./UsagePresentation.ts";

export function UsageComposition({ items, label }: { items: UsageItem[]; label: string }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const [unavailable, setUnavailable] = useState(false);
  const total = items.reduce((sum, item) => sum + item.totals.costUsd, 0);
  useEffect(() => {
    if (total === 0) return;
    let disposed = false;
    let chart: Chart<"bar", number[], string> | undefined;
    void import("./UsageChart.ts")
      .then(({ Chart }) => {
        if (disposed || !canvas.current) return;
        const colors = usageColors(items.map((item) => item.key));
        chart = new Chart<"bar", number[], string>(canvas.current, {
          type: "bar",
          data: {
            labels: [label],
            datasets: items.map((item) => ({
              label: `${item.label}: ${formatUsageCost(item.totals)}`,
              data: [item.totals.costUsd],
              backgroundColor: colors[item.key],
              barThickness: 9,
              borderWidth: 0,
            })),
          },
          options: {
            indexAxis: "y",
            responsive: true,
            maintainAspectRatio: false,
            animation: false,
            scales: {
              x: { stacked: true, display: false, min: 0, max: total },
              y: { stacked: true, display: false },
            },
            plugins: {
              tooltip: { enabled: false },
            },
          },
        });
      })
      .catch(() => {
        if (!disposed) setUnavailable(true);
      });
    return () => {
      disposed = true;
      chart?.destroy();
    };
  }, [items, label, total]);

  if (total === 0) return <p class="usage-muted">No cost to plot</p>;
  if (unavailable) return null;
  return (
    <div class="usage-composition">
      <canvas
        ref={canvas}
        role="img"
        aria-label={`${label}: ${items.map((item) => `${item.label} ${formatUsageCost(item.totals)}`).join(", ")}`}
      />
    </div>
  );
}
