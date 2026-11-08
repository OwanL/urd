# Development workflow

- Keep verification local. Do not add CI workflows or CI-only tooling unless the owner explicitly requests it.

# style guide

- Use plain code documentation, avoid xml docs.
- Code should self documenting, for example, am I able to understand what code does by reading it? ie Session.create() reads quite nicely, where HarnessServiceExecutor.instantiateAsync() means almost nothing to a user without prior knowlege.
- Prefer low levels of scope nesting. Deeply nested code is hard to read and reason about.
- Name private C# fields in camelCase without underscore prefixes; use `this.` only to resolve ambiguity with parameters or locals.
- Use two spaces per indentation level, not literal tabs; follow `.editorconfig`.
- Use same-line opening braces in C# (K&R style).
- Omit braces for short, clear single-statement bodies; keep them when nesting, comments or multiline bodies make grouping clearer.
- One type per file, except small data-only types (records, enums) of one concept, which share a file named for that concept.
- Code should be performant. Urd needs to have a low memory overhead, and be snappy, and quick.
- Follow existing codebase conventions. Ground yourself in this before writing any code.

# UI guidance

- Build a minimal, low-noise UI. Avoid unnecessary instructions, secondary text and
  explanatory labels; do not add a subtitle to every control or panel. User-facing
  copy must be precise, blunt and informational, without marketing prose or filler.
- Prefer icons with tooltips for identifying familiar categories, such as tool-call
  kinds. Use a short label where an icon would be ambiguous. Icon-only controls
  still need accessible names.
- Reveal detail through meaningful information hierarchies instead of showing
  everything permanently. Rich, lightweight tooltips can expose substantial detail:
  for example, a cost total can reveal history with per-point accounting breakdowns.
- Treat interactive hover details as stable popovers, not fragile hover-only text.
  Keep them reachable while the pointer moves into their content or nested detail;
  provide keyboard and click access, with predictable dismissal.
- Pay meticulous attention to micro-interactions: menu and submenu hit regions,
  hover intent, focus, opening and dismissal. Users should not need precise pointer
  gymnastics to reach a nested menu or keep it open.
- Avoid unsolicited popups and modal interruptions. User-invoked menus and detail
  panels are welcome. Reserve confirmation for consequential mistakes that cannot
  reasonably be recovered from; prefer recovery or Undo where genuinely supported.
- Preserve reading position, focus and drafts. Appending, deleting or updating rows
  must not unexpectedly move the content being read. Stabilise layout and anchor
  scrolling first; a smoothly animated jump still disrupts reading.
- Use subtle, understated, smooth animation to explain changes and maintain
  continuity. Respect reduced-motion preferences. Animation must not delay actions
  or substitute for stable layout.
- Acknowledge interactions immediately, without waiting for network work or an
  animation. A half-second delay before visual feedback is unacceptable. Brief
  hover-intent delays may prevent accidental flyouts, not excuse sluggish controls.
- Keep feedback truthful: distinguish pending, running, completed, failed and
  uncertain states. Acknowledging an interaction does not mean its work is complete.
- Make visual weight and hierarchy deliberate through typography, spacing, grouping
  and separation. Use consistent controls and semantic colour roles. Secondary
  information must not compete with the main task; do not achieve density by
  sacrificing legibility or usable hit targets.
- Prefer intuitive, composable interactions that expose richer behaviour without
  adding permanent chrome, such as direct manipulation through dragging. Give
  essential actions a keyboard-accessible alternative to gestures.
- Keyboard operation, visible focus, adequate contrast and usable zoom are baseline
  requirements. Do not communicate important state through colour alone.
- Depth on demand should also mean resource use on demand. Load and mount expensive
  display features when needed; hidden views must not retain unbounded data or run
  unnecessary background work. Keep input and navigation responsive in long sessions.
- Pie is a visual and behavioural reference, not the guidelines themselves. Judge
  its patterns against these principles; do not inherit its architecture or copy
  its UI code and styles.
