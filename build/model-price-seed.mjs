// Pass a saved Models.dev v2 public chat feed and its UTC capture date. Writes the seed to stdout.
import { readFileSync } from 'node:fs';

const [feedPath, captureDate] = process.argv.slice(2);
if (!feedPath || !/^\d{4}-\d{2}-\d{2}$/.test(captureDate ?? '')) {
  throw new Error('Usage: node build/model-price-seed.mjs <feed.json> <YYYY-MM-DD>');
}

const feed = JSON.parse(readFileSync(feedPath, 'utf8'));
const primary = new Set(['openai', 'github-copilot', 'ollama-cloud']);
const benchmarks = new Set(['openrouter', 'mistral']);
const needed = new Set();
for (const provider of primary) {
  for (const model of Object.values(feed[provider].models)) {
    if (model.type === 'chat') needed.add(model.canonical_id ?? `${provider}/${model.id}`);
  }
}

const capturedAt = new Date(`${captureDate}T00:00:00Z`).toISOString();
const entries = [];
for (const provider of Object.keys(feed).sort()) {
  const rows = Object.entries(feed[provider].models).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0);
  const selected = [];
  for (const [key, model] of rows) {
    if (model.type !== 'chat') continue;
    const canonical = model.canonical_id ?? `${provider}/${model.id}`;
    const maker = canonical.split('/')[0];
    if (!primary.has(provider) && (!needed.has(canonical) || (provider !== maker && !benchmarks.has(provider)))) continue;
    const { id, canonical_id, type, name, cost } = model;
    const { text, multipliers } = cost ?? {};
    selected.push(`      ${JSON.stringify(key)}: ${JSON.stringify({ id, canonical_id, type, name, cost: text ? { text, multipliers } : undefined })}`);
  }
  if (selected.length) entries.push(`    ${JSON.stringify(provider)}: {"models": {\n${selected.join(',\n')}\n    }}`);
}

process.stdout.write(`{\n  "capturedAt": ${JSON.stringify(capturedAt)},\n  "providers": {\n${entries.join(',\n')}\n  }\n}\n`);
