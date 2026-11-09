# MCP client seam qualification

**2026-11-04 — bounded experiment, not a production implementation.** The owner
already selected `ModelContextProtocol.Core`; this does not reopen that choice,
lock its version, or select a `ToolExecutor` API/state design. Requirements:
[MCP research](mcp-library-research.md), [current-authority dispatch](discovery-design.md)
and [session lifetimes](session-design.md).

## Scope and evidence

**27 focused checks passed** in `run-1af1bc27`; build had zero warnings/errors.
The fixture uses the actual official NuGet
`ModelContextProtocol.Core` **2.3.1** (package source commit
`61420937cbef3eab895cafc8a89418811fb71f54`) on Windows, .NET SDK **10.0.403** /
runtime **10.0.14**. Its only MCP peers are two sequential, owned fake stdio
processes; the second owns one inert child. Counters and synthetic JSON only.

Explicitly pinning protocol **2025-11-25** exercises `initialize` / `initialized`;
it does **not** test default 2026 discovery/fallback. No provider model, configured
user integration, credentials, OAuth, HTTP listener or external service is used.
NuGet artifact retrieval is the only experiment network access. Existing cached
MEAI dependencies and installed toolchain are reused; no machine installation.

## Verified behavior

| Case | Observed result |
| --- | --- |
| Initialize, list, invoke | Explicit handshake and server identity; two tool pages aggregated in order; counted invocation succeeded. |
| Result fidelity | Text/image blocks, content/result metadata, structured JSON and `isError` matched the peer result. Typed DTO discarded an unknown result extension; a small transport decorator retained it in the parsed envelope with the RPC ID. Not byte-exact capture. |
| Error distinction | `isError=true` remained a result; JSON-RPC `-32602` became `McpProtocolException`. Array-root error data disappeared from `Exception.Data`; client envelope capture retained that array, RPC ID and integer `9007199254740993` exactly. |
| Argument boundary | The SDK sent a schema-invalid numeric argument. The fake peer rejected it before incrementing; a client SDK is not Urd's argument validator. |
| Current authority | Permission revocation, wrong source identity, changed schema and removal each blocked dispatch without changing the peer's call/effect counters. Old exposure stayed unchanged. Negative control: directly invoking the retained SDK tool **still reached the peer after removal**. |
| Cancellation | Local call threw `OperationCanceledException`; server remained usable. A gated counter operation acted before cancellation and continued after the caller observed cancellation; no result/retry followed. No cancellation notification was observed before the subsequent stats response in this run—neither receipt nor remote stop follows from local cancellation. |
| Disposal/ownership | Cooperative peer observed stdin EOF and exited 0. A second peer ignored EOF; with a 500 ms grace timeout, SDK disposal stopped it and its one inert descendant in **586 ms**. Process handles were retained and exits checked. This is one Windows observation, not a universal disposal deadline/process-tree guarantee. |

Independent source/method and final evidence/claim reviews approved the bounded
claims. All owned processes were confirmed stopped and the heavy-work lease
released; no user interaction or credential cleanup is needed.

## What the boundary needs

- Keep a backend-owned MCP client/transport alive independently of individual
  requests or browser connections; choose process command/environment deliberately.
- Retain immutable exposure plus configured server/tool identity. At dispatch,
  check the **current** binding, definition and permission. A retained SDK tool
  object is an invocation handle, not permission. The fixture's few local checks
  are composition-only, not a production catalog, validator or approval design.
- Prefer `CallToolAsync` / `McpClientTool.CallAsync` when complete typed MCP outcomes
  are needed. Keep `isError`, protocol exceptions and cancellation distinct; do
  not flatten everything to text or assume cancellation means no effect.
- Preserve provider outer call identity separately from MCP JSON-RPC identity.
  Typed results preserve known fields, not original bytes/arbitrary extensions.
  The fixture captures parsed client transport envelopes where exact result/error
  payload identity is needed; full production capture/persistence is not designed.
- Never automatically repeat a cancelled/uncertain effect. Disposal and observed
  process exit settle local ownership, not undo work or establish exactly-once
  external behavior.

## Limits

No real-server interoperability, HTTP/SSE, 2026 subscriptions, OAuth, reconnect,
crash/restart persistence, native/wrapper model reliability, concurrent authority
changes, detached subprocesses or non-Windows runtime coverage. Explicit relisting
is tested, not continuous freshness. A local authority check cannot atomically
freeze a remote server between list and call. SDK/library selection remains intact;
production lifetime/admission/recording integration remains separate work.

## Reproduction

Retained disposable fixture (not repository source):
`C:/Users/OwanLazic/AppData/Local/Temp/urd-foundation-streams-6268b517/mcp-qualification-71ad8321/`.
`verify.ps1 -LeaseId <started-lease>` requires exclusive `urd:heavy-local-work`;
it restores only the fixture, builds single-worker without compiler/build servers,
and runs the sequential peers. Source, package manifest, peer/parsed-client envelopes
and focused logs remain there for inspection; they are local temporary evidence,
not a distributed production/test project. Decisive artifacts: `build.log`,
`run.log`, and under `run-1af1bc27/`: `checks.json`,
`cooperative/{wire,client-envelopes}.jsonl`, `cancellation-notification.txt`,
`forced-dispose-ms.txt`.
