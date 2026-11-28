import { HoverPopover } from "../../../controls/HoverPopover.tsx";
import { Icon } from "../../../controls/Icon.tsx";
import type { SubagentCost } from "../../../sessions/SubagentCall.ts";
import { formatCostUsd } from "../../../usage/UsagePresentation.ts";
import { subagentCostLabel } from "./SubagentMetrics.ts";

export function SubagentCostControl({
  cost,
  visible,
}: {
  cost: SubagentCost | undefined;
  visible: boolean;
}) {
  if (!cost || (cost.total.usd === null && !cost.total.partial && !cost.hasDescendants))
    return (
      <span class="subagent-cost subagent-cost-missing" title="Cost not available">
        —
      </span>
    );

  const amount = cost.total.usd === null ? "unavailable" : formatCostUsd(cost.total.usd);
  const label = `Subagent cost: ${amount}${cost.hasDescendants ? ", includes descendants" : ""}${cost.total.partial ? ", incomplete" : ""}`;
  return (
    <span class="subagent-cost">
      <HoverPopover
        label={label}
        surfaceLabel="Subagent cost breakdown"
        class="subagent-metric-control"
        surfaceClass="subagent-cost-popover"
        enabled={visible}
        content={
          <>
            <strong>Cost breakdown</strong>
            <dl class="subagent-cost-breakdown">
              <dt>This subagent</dt>
              <dd>{formatCostUsd(cost.own.usd)}</dd>
              {cost.hasDescendants && (
                <>
                  <dt>Nested subagents</dt>
                  <dd>{formatCostUsd(cost.descendants.usd)}</dd>
                </>
              )}
              <dt class="subagent-cost-total">{cost.total.partial ? "Subtotal" : "Total"}</dt>
              <dd class="subagent-cost-total">{formatCostUsd(cost.total.usd)}</dd>
            </dl>
            {cost.total.partial && <p>Some usage data is incomplete.</p>}
          </>
        }
      >
        {cost.hasDescendants && <Icon name="layers" />}
        <span class="subagent-cost-amount">{subagentCostLabel(cost.total)}</span>
      </HoverPopover>
    </span>
  );
}
