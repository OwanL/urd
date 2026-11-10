# Agent tools — design decisions

**Status: in owner design review (started 2026-11-10).** Records agreed decisions as they are
made. Class shapes and contracts are still to be proposed; no implementation is authorized yet.
Related: [execution](execution-design.md), [sessions](session-design.md), [discovery](discovery-design.md).

## Agreed

1. **Authority.** Full access, no approval prompts. Tools run as the user on the backend machine.
   A chat's working folder is the default for relative paths, not a boundary. Approval, if ever
   wanted, is a pre-dispatch check added later; it is not designed now.
2. **Starting tool set.** Follow Pi: `bash`, `read`, `write`, `edit`. Copy Pi's behavior where
   it is clear-cut.
3. **One implementation, per-family surfaces.** Models carry model-family metadata. A tool has
   one implementation; core tools (`read`, `write`, `edit`, possibly `bash`) may own an opt-in,
   lightweight surface per family (name, schema, description, argument/result translation) so
   each family gets the shape it was trained on. Bespoke tools have one simple, well-documented
   surface. Context pruning must account for surfaces.
4. **Raw calls only.** Store each call as the model emitted it. The stored tool name identifies
   the surface, and that surface interprets its own raw calls for the UI and pruning. No
   canonical copy. Switching model family mid-chat is allowed and is the user's responsibility;
   no conversion layer.
5. **Shell.** `bash` via Git for Windows (as Pi), launched as `bash -c` — never a login shell
   (measured ~690 ms vs ~60 ms per call on the owner's machine). Urd supplies the environment.
   Missing Git Bash is a clear startup/tool error. A persistent shell is deferred. Calls record
   their duration.
6. **Parallel calls.** Calls emitted in one reply run in parallel; results return in call order;
   each call returns its own result — a failure never cancels siblings. Mutations of the same
   file are serialized (Pi's per-file queue); a read of a file waits for pending mutations to it.
   No batch/meta tool: evidence shows models misuse them and harnesses removed them. If order
   matters, the model uses separate replies or chains commands in bash. Script-based tool
   orchestration ("code mode") is possible future work, not this scope.
7. **Background calls.** Any tool call may set `background: true`. The harness provides this
   generically; tool implementations are unaware of it. The call immediately gets a placeholder
   result naming the pending call; the model keeps working. The real result is appended as a
   message at the next opportune point — after the current set of tool results (providers
   require results to follow their calls directly), or by starting a new turn if the agent is
   idle. Background calls are for finite work.
8. **Background-call control.** Two tools: list pending calls, and cancel all pending calls.
9. **Long-lived processes** (dev servers) use plain bash backgrounding (`cmd > log 2>&1 &`),
   read with `read`, stopped with `kill` — as Pi. Required process rules:
   - a bash call completes when the shell exits, not when all output pipes close;
   - processes left behind outlive the call and belong to the chat;
   - they are killed when the chat closes or Urd exits (one Windows job object per chat, so a
     crash also cleans up).
10. **Stop** cancels the current run's work including its pending background calls; it does not
    kill processes deliberately left running by earlier bash calls.

11. **Tool behavior details** (output truncation and spill files, `read` paging and images,
    `write` folder creation, `edit` exact-match rules, timeouts) copy Pi as-is.
12. **Crash.** A call interrupted by an Urd crash, background or not, is reported to the model
    as interrupted and possibly partly applied. It is never re-run.

## Open

- Model-family metadata location and the initial families.
- Class structure and contracts: tool, surface, background-call registry, process ownership.
- Transcript presentation of tool rows and pending background calls.
