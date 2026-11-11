# System guidance

Edit the text returned by `GetText` in these files. Prompt wording belongs here,
not in the context manager.

| File | Applies to | Order |
| --- | --- | --- |
| `BaseGuidance.cs` | All sessions | 0 |
| `PrimaryGuidance.cs` | Primary sessions | 100 |
| `SubagentGuidance.cs` | Subagents | 100 |
| `WorkingDirectoryGuidance.cs` | Sessions with a working directory | 200 |
| `AssignmentGuidance.cs` | Sessions with an assignment | 300 |

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
