# System guidance

Edit the text returned by `GetText` in these files. Prompt wording belongs here,
not in the context builder. Applicable built-in instructions always bypass pruning.

| File | Applies to | Order |
| --- | --- | --- |
| `BaseGuidance.cs` | All sessions | 0 |
| `DefaultWorkingGuidance.cs` | Primary sessions | 10 |
| `WorkingPreferencesGuidance.cs` | Primary sessions | 20 |
| `ToolUsageGuidance.cs` | Sessions with a working directory | 50 |
| `DocumentationGuidance.cs` | All sessions | 60 |
| `PrimaryGuidance.cs` | Primary sessions | 100 |
| `SubagentGuidance.cs` | Subagents | 100 |
| `SubagentPreferencesGuidance.cs` | Subagents | 110 |
| `WorkingDirectoryGuidance.cs` | Sessions with a working directory | 200 |
| `SkillsGuidance.cs` | Sessions with a working directory and skills | 220 |
| `AssignmentGuidance.cs` | Sessions with an assignment | 300 |

## Guidance copied from Pie

The working defaults/preferences, child guidance and tool-use/documentation rules
were selected from Pie's `harness/agent-instructions/` and
`harness/pi/packages/coding-agent/src/core/`. Source paths are recorded on each class.
Retained text is word-for-word, including spelling and punctuation. Overlapping
advice has been removed: defaults hold common rules, and preferences retain distinct
requirements. These are local editable copies, not a runtime dependency or automatic
synchronization with Pie.

Omissions include Pie/Pi identity and documentation paths, skill/resource recovery,
session coordination, subagent dispatch, unavailable clarification/computer-control
tools, timers and unsupported image-presentation workflows. The Pie-naming sentence
in the defaults preamble and the Pie-specific repository-layout sentence were omitted. The implicit push-approval
example was omitted while retaining its preceding intent sentence; explicit consent
limits remain. Primary preferences are not included in child sessions. Child guidance
is composed for `Subagent` inputs; it does not add a delegation runtime.

## Add a block

Add a concrete `StartingContextContribution` subclass with a parameterless constructor.
`StartingContextBuilder` discovers it automatically in Urd Core's assembly; there is no registration
list. Keep guidance constructors free of I/O or background work.

```csharp
namespace Urd.Core.Context;

// Extra instructions for primary sessions.
internal sealed class ExampleGuidance : StartingContextContribution {
  public override int Order => 150;

  public override bool AppliesTo(StartingContextInput input) =>
      input.SessionKind == SessionKind.Primary;

  public override string? GetText(StartingContextInput input) => """
    Your instructions here.
    """;
}
```

Omit `AppliesTo` to include a block in every session. `GetText` receives the session
inputs, so guidance can include working-directory or assignment facts. Return null
or whitespace to omit the block. Each included block becomes one system message.
Guidance failures stop composition rather than silently dropping instructions.

Blocks sort by ascending `Order`, then ordinal full type name. Multiple instances
of the same type and order retain their supplied order. Types are discovered once;
instances are created per builder, not shared globally.

`runtime/` contains the base type, input/output records, discovery and builder.
The builder also accepts supplied contributions. Parameterized-only classes are not
auto-created: `DiscoveredInstructionDocument` carries loaded text/path/scope, with no
file I/O in its constructor or rendering. `SkillsGuidance` formats the supplied selection.

Sessions retain exact saved instruction/tool snapshots for each initial or successful
compaction boundary. Ordinary requests, reopening and model/preference changes do not
rediscover resources. The first prompt and first request after successful compaction
prepare a selection; compaction alone does not. Built-ins bypass selection, while
protected discovered resources are excluded from candidate input and always retained.
Instruction and skill selectors receive each candidate's file path and use it as a signal
of applicability to Urd, not as an automatic exclusion.
See [the pruning plan](../../../../docs/pruning-plan.md) for task inputs and failure rules.

## Project instruction documents

`InstructionResourceDiscovery` asks the shared catalog for matching files in the session's
working directory and descendants. The working directory, not a Git root, is the boundary:
no ancestor or sibling project files are included. Git is not required; hidden, ignored and
untracked files are eligible. Discovery recognizes these filenames case-insensitively:
`AGENTS.md`, `APPEND_SYSTEM.md`, `APPEND-SYSTEM.md`, `AGENT_INSTRUCTIONS.md`,
`AGENT-INSTRUCTIONS.md`, `AGENT_INSTRUCTION.md`, `AGENT-INSTRUCTION.md`,
`INSTRUCTIONS_FOR_AGENTS.md` and `INSTRUCTIONS-FOR-AGENTS.md`. `SKILL.md` files are
catalogued separately. Other harnesses' files, including `CLAUDE.md`, `GEMINI.md`, Copilot
`*.instructions.md`/`copilot-*` and their configuration directories, are excluded.

One catalog per `UrdStore` serves session preparation and the Settings resource picker.
It indexes paths only; it does not cache instruction text or skill frontmatter.
The backend's derived SQLite index is `<DataRoot>/urd.sqlite.instructions.sqlite`
(the session database is `<DataRoot>/urd.sqlite`). It survives backend restarts: last-known
rows are served immediately while a single-flight reconcile scan runs in the background, and
cold roots wait for a scan to produce its first matches or finish, then serve partial results
while it runs. Startup warms the saved default working directory on a best-effort basis;
other roots scan on demand. The catalog watches active roots for file and directory creation,
deletion and renames, including moves of populated directories. Missing or lost watcher
coverage triggers reconciliation; last-known rows can still be served but are not proof of
freshness. A cold caller still waiting for its first matches or scan completion observes a
scan failure; a caller that already received partial results may not see that failure.
Requests return paths with an index state (`DiscoveredInstructionResources.Index`):
complete, refreshing or partial, plus the number of files matched so far. A caller cancelling
its wait does not cancel a scan shared with others. The catalog drains its watchers and
scans on backend shutdown. Schema or integrity problems trigger a rebuild only when the index is
identified as Urd's catalog. A nonempty foreign file is left untouched and causes an error; a fresh
empty SQLite file can be initialized as the derived cache.

The scanner is bundled `fd` v10.3.0, resolved beside the backend under
`fd/<platform>-<architecture>/`; it is not looked up on `PATH`. It searches exact eligible
basenames, including hidden and ignored files, emits NUL-delimited paths and does not follow
links. `$Recycle.Bin`, `node_modules`, `bin`, `obj` and the OS temp directory are excluded below
the scan root and from watcher work. An explicit root at or inside an otherwise excluded
directory is still scanned. Mount-aware POSIX scans reject unknown or remote filesystems and
skip virtual OS trees. Project files are checked against the actual opened path before reading.
Unsafe or out-of-scope roots return no project candidates; unsupported-platform, scanner and
index failures are surfaced rather than treated as successful empty scans. Packaged skills remain available independently of the
working directory. Checked-in scanner binaries cover Windows, macOS and Linux x64/arm64
targets; asset presence is not published-layout or end-to-end qualification on every target.
See [fd packaging](../../../vendor/fd/README.md) for the asset manifest and notices.

Documents carry full text and explicit directory scope when loaded for preparation. All
discovered documents are selection candidates; retained documents render shallower scopes
first, then ordinal canonical paths, with a scope/source header so nested rules apply only
within that directory. The main context preserves original text rather than accepting
model-written replacements. Instruction/skill protection identities are full canonical
file paths (case-insensitive on Windows); tool identities are registered names. Protection
does not grant tool authority.

## Skills

`SkillsGuidance` formats selected skills by name, description and path, using Pi's wording;
the model reads a skill's file when a task matches. Project `SKILL.md` files are found under
the working directory without Git. Project and packaged skills are both candidates, including
same-name entries. When a project skill is selected, the packaged same-name entry is omitted;
a protected packaged entry is retained. Skill bodies are not supplied to the selector.
The selector lists each exact path once and groups paths with identical names and
descriptions; it can still select any of those paths independently.

Add a packaged skill as `skills/<name>/SKILL.md` with `name` and `description`
frontmatter. The build copies `skills/` beside the server so the model can read it.
`diagnose` is adapted from Pie's skill of the same name with Pie-specific passages removed.
