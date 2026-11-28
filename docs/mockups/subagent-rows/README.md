# Subagent rows

[Interactive mockup](index.html) · [Wide and narrow panes](preview.png) · [Narrow pane with cost breakdown](narrow.png)

Keep the title, model, elapsed time, and total cost on one line at every width.
Align the metrics across rows. In a narrow pane, use shorter model labels and
truncate long titles rather than wrapping the row or hiding elapsed time or cost.
Open the subagent from its title to read the report; don’t add an inline preview.

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
