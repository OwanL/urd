# System guidance

Edit the text returned by `GetText` in these files. Prompt wording belongs here,
not in the context manager.

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

Add a concrete `ContextGuidance` subclass with a parameterless constructor. The
manager discovers it automatically in Urd Core's assembly; there is no registration
list. Keep guidance constructors free of I/O or background work.

```csharp
namespace Urd.Core.Context;

// Extra instructions for primary sessions.
internal sealed class ExampleGuidance : ContextGuidance {
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
instances are created per manager, not shared globally.

`runtime/` contains the base type, input/output records and context manager. The
manager also accepts explicitly supplied guidance instances. Parameterized-only
classes are not auto-created: a future `AGENTS.md` loader can construct text-backed
instances through that path. File discovery/loading is not implemented here.

Existing chats retain their saved prompts. Rebuild/restart Urd and start a new chat
to use guidance changes.

## Skills

`SkillsGuidance` lists skills by name, description and path, using Pi's wording; the
model reads a skill's file when a task matches. It lists `SKILL.md` files tracked by
Git under the working directory, then skills packaged in `skills/`. A project skill
replaces a packaged skill with the same name. Outside a Git repository only packaged
skills are listed. Skills are found once, when a chat's starting context is composed.

Add a packaged skill as `skills/<name>/SKILL.md` with `name` and `description`
frontmatter. The build copies `skills/` beside the server so the model can read it.
`diagnose` is adapted from Pie's skill of the same name with Pie-specific passages removed.
