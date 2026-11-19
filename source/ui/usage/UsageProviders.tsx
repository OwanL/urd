import { HoverPopover } from "../controls/HoverPopover.tsx";
import type { UsagePoint } from "./Usage.ts";
import type { UsageProvider } from "./UsageAggregation.ts";
import { usageProviders } from "./UsageAggregation.ts";
import { formatUsageCost, formatUsageTime, usageColors } from "./UsagePresentation.ts";

export function UsageProviders({
  providers,
  onInspect,
}: {
  providers: UsageProvider[];
  onInspect: () => void;
}) {
  const colors = usageColors(providers.map((provider) => provider.key));
  return (
    <div class="usage-provider-legend" role="group" aria-label="Providers">
      {providers.map((provider) => (
        <HoverPopover
          key={provider.key}
          label={`${provider.label} ${formatUsageCost(provider.totals)}; model breakdown`}
          surfaceLabel={`${provider.label} models`}
          class="usage-provider-trigger"
          surfaceClass="usage-model-popover"
          onReveal={onInspect}
          content={
            <div class="usage-details">
              <header class="usage-row usage-heading">
                <strong>{provider.label}</strong>
                <strong>{formatUsageCost(provider.totals)}</strong>
              </header>
              <ModelRows provider={provider} />
            </div>
          }
        >
          <span class="usage-swatch" style={{ background: colors[provider.key] }} />
          <span class="usage-provider-label">{provider.label}</span>
          <span>{formatUsageCost(provider.totals)}</span>
        </HoverPopover>
      ))}
    </div>
  );
}

export function UsagePointDetails({
  point,
  colors,
}: {
  point: UsagePoint;
  colors: Record<string, string>;
}) {
  return (
    <div class="usage-details">
      <header class="usage-row usage-heading">
        <span>{formatUsageTime(point.at)}</span>
        <strong>{formatUsageCost(point.totals)}</strong>
      </header>
      <UsageProviderBreakdown providers={usageProviders(point.breakdown)} colors={colors} />
    </div>
  );
}

export function UsageProviderBreakdown({
  providers,
  colors = usageColors(providers.map((provider) => provider.key)),
}: {
  providers: UsageProvider[];
  colors?: Record<string, string>;
}) {
  return (
    <div class="usage-provider-breakdown">
      {providers
        .filter((provider) => provider.totals.requests > 0)
        .map((provider) => (
          <section class="usage-provider-section" key={provider.key}>
            <div class="usage-row">
              <span class="usage-provider-name">
                <span class="usage-swatch" style={{ background: colors[provider.key] }} />
                {provider.label}
              </span>
              <span>{formatUsageCost(provider.totals)}</span>
            </div>
            <ModelRows provider={provider} />
          </section>
        ))}
    </div>
  );
}

function ModelRows({ provider }: { provider: UsageProvider }) {
  return (
    <dl class="usage-model-list">
      {provider.models
        .filter((model) => model.totals.requests > 0)
        .map((model) => (
          <div key={model.key}>
            <dt>{model.label}</dt>
            <dd>{formatUsageCost(model.totals)}</dd>
          </div>
        ))}
    </dl>
  );
}
