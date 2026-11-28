# Subagent rows

[Interactive mockup](index.html) · [Wide comparison](preview.png) · [Narrow view with cost breakdown](narrow.png)

**Recommendation: A, aligned metrics.** On wide panes, align the model, elapsed
time, and total cost so adjacent rows are easy to compare. When space runs out,
move the same metadata below the title. **B** does that at every width: it gives
long titles more room, but takes more vertical space in a batch. In both layouts,
keep the existing title navigation and report below the row.

## What the numbers mean

- **Cost** is this subagent’s own cost plus all of its descendants, recursively,
  counted once. The layers icon marks a total that includes descendant cost.
  Select the amount to see “This subagent”, “Nested subagents”, and “Total”.
  While work is ongoing, show cost so far. Don’t show missing prices as zero or a
  known subtotal as a complete total.
- **Elapsed** is wall-clock time since this subagent started, frozen when its run
  ends. It isn’t summed across descendants. A queued run shows “Queued” rather
  than `0:00`; a running row pairs its time with a running indicator.
- **Model** names the model on this row, not the models used by its descendants.

This is a standalone design mockup with synthetic, fixed values. Only the cost
breakdown is interactive. The rolled-up cost is proposed for these rows; the
existing session cost indicator is unchanged.
