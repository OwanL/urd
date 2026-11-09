# Context/exposure lifetime evidence

**2026-11-04 — bounded research and one synthetic adapter experiment.** No context
engine, production schema/API, reset policy or package pin is selected here.
[ARCHITECTURE](ARCHITECTURE.md#preserve-model-visible-prefixes-during-ordinary-continuation-and-discovery)
owns the requirements; [Discovery Design](discovery-design.md) owns the existing
exposure direction. Direct `IChatClient`, retained AI pruning and **qualified native
additions, otherwise an initial stable wrapper** remain intact.

**Main finding:** ordinary `ChatMessage` JSON reload can change an already-sent
tool result's actual model-facing representation, even after native definitions,
reasoning and call IDs are restored. Text gains JSON quoting; typed text/image
blocks become JSON text. Earlier evidence did not test this transition. This is a
qualification gap to fix in the eventual context owner—not a reason to replace
`IChatClient` or adopt the fixture's storage technique.

## Lifecycle ledger

These are distinct responsibilities, not proposed classes or record formats.
“Prefix” includes instructions, ordered definitions and earlier input content;
append-only transcript rows alone do not preserve it.

| Transition | Model-visible prefix | Necessary native material | Original readable history | Current execution authority |
| --- | --- | --- | --- | --- |
| **Initial guidance/catalog → AI selection** | Assemble actual guidance and selected metadata before first send. Exclude protected entries **before** pruning and protect them when applying results. | Complete selected schemas/order; route-required representation. Initial wrapper only on the selected fallback path. | Skill metadata is not a full skill body. Do not invent an archive of scorer candidates. | Protected does not override disabled/unavailable or child-disallowed capabilities. |
| **Dynamic tool recovery** | Append qualified native definitions or wrapper disclosures; do not rebuild earlier definitions. | Preserve definition position/version, provider call ID and outer versus resolved identity. | Keep the actual discovery exchange while the session remains open. | Resolve against current executable binding/permissions, not remembered exposure. |
| **Dynamic skill recovery** | Append the loaded body as new result content, rather than rewriting earlier instructions merely because it loaded. | Preserve its result representation, including literal text. | Retain what was delivered, not a later reread of a changed file masquerading as the old result. | Skill prose does not grant tools or override restrictions. |
| **Ordinary continuation/reload** | Preserve **all** already-sent components, including tool results. Avoid date/catalog regeneration masquerading as continuity. | Reconstruct exact required IDs, reasoning, definitions and result shapes; generic JSON is insufficient here. | Keep readable originals; reopening/browsing is not effect replay. | Rebuild live bindings independently; stale/incompatible definitions can deny execution. |
| **New-result reduction / raw recall** | Transform only a new result before sending; raw recall appends content, never rewrites a previously sent result. | Preserve the chosen model-facing result distinctly from raw execution data when they differ. | Recall lifetime/fidelity must be explicit; it is not permanent analytics. | Apply resolved-target processing once for direct/wrapper calls. |
| **Earlier-prompt edit** | Deliberately replace suffix/context; this is not ordinary continuation. | Invalidate removed identities/disclosures rather than reusing stale positional references. | Removed tail is replaced; external effects and separately needed coordination facts are not undone. | Removed context cannot silently confer authority; current restrictions remain. |
| **Compaction success/failure** | Success changes the working view; failure must leave usable context. A boundary does not inherently mean rerunning pruning. | Needed definitions, call/result pairing and opaque material cannot depend on a summary remembering them. | Summarized originals remain readable until closure. | Summary prose is not evidence of disclosure/permission. |
| **Provider/model or explicit guidance/resource reset** | May require deliberate reprojection; preserve unchanged parts where feasible, not a blanket immutable-session rule. | Foreign encrypted/native content is not presumed portable. | Do not erase originals or replay tools to manufacture compatibility. | New route capability and live restrictions apply; no widened provider/billing pool. |

The last rows identify **open reset semantics**, not new approvals. Cross-session
attachment/result references must remain valid for any model projection still
using them; readable history and model context are different consumers.

## Reference behavior worth preserving—not machinery to inherit

Read-only Pie inspection at **`cc82abb0f317f31341d3a6f12aa1545f6be1fbea`** found:

- Guidance discovery is ancestor-scoped; skill catalogs contain metadata and paths,
  not every body. Prompt assembly orders base/append/project context/skill guidance.
  This establishes a reference path, not a newly selected Urd precedence algorithm.
- `request_capability` reads a skill body into a tool result. Tool recovery returns
  a descriptor through the recovery controller, not a native-catalog mutation.
- Pie distinguishes **resetting model context from reselecting capabilities**.
  Model/settings/resource boundaries retain an existing selection; successful
  compaction can enable refresh, and disabling refresh retains selection, not the
  old pre-compaction history. Ordinary reopen restores the compatible frozen period.
- Tool-result reduction is separate again: Pie rewrites only new results. Lossy
  reduction requires recoverable **post-SDK-truncation, pre-reduction** text;
  `read` and recovery descriptors bypass reduction. The stash cannot recover earlier
  SDK truncation. Its algorithms, token threshold and temporary-file policy are
  not automatically Urd requirements.

Exact source anchors, relative to Pie: `harness/agent-instructions/README.md:3–5`;
`harness/pi/packages/coding-agent/src/core/system-prompt.ts:149–172`;
`harness/tool-and-skill-selection/lifecycle/register.ts:420–449`;
`harness/tools/request-capability/index.ts:55–58,138–154,172–197`;
`harness/tool-and-skill-selection/state/context-period-state.ts:136–190`;
`docs/plans/IMMUTABLE-MODEL-CONTEXT.md:23–53,212–217`;
`docs/contracts/TOOL-RESULT-PRUNING.md:14–21,91–106`.
These are source/contract observations, not installed-host or provider-cache proof.

## New experiment: already-sent results → reload → next request

[Execution Design's composed probe](execution-design.md#composed-follow-up-adapter--guarded-urd-loop--sqlite--continuation)
reloaded **before the first transmission of its tool result**. Its unchanged-prefix
check covered the first three input items; its result check JSON-decoded the output
string. That established useful composition but not exact preservation of a result
already sent earlier. The new fixture targets only this missing transition.

Windows, .NET SDK **10.0.403** / runtime **10.0.14**, MEAI OpenAI adapter **10.11.0**,
OpenAI **2.14.0**. These remain experimental pins. A shared request-only client made
three calls to an owned fake loopback HTTP/SSE endpoint:

1. Send synthetic completed call/result pairs with intact runtime types.
2. Append the scripted assistant response/user turn, save/reload ordinary message
   JSON plus the **previously established** native-item reconstruction, then resend.
3. Replay the exact first-sent native result items as a **test-only alternative**.
   No model/tool loop, pruner, SQLite or actual effect execution was included.

| Result supplied before reload | Actual serialized output after generic reload |
| --- | --- |
| Literal text containing quotes/newline | Different text: added JSON quotes/escaping. |
| Text that happens to look like JSON | Also quoted; parsing it into an object would change its original meaning/type. |
| Structured JSON object | Same output string in this case—a useful non-failing control, not universal object fidelity. |
| Typed text + image `AIContent` list | Native output **array became a string** containing serialized .NET content records; no longer an image block. |

The source-supported cause is the adapter's runtime-type dispatch in
[`ToOpenAIResponseItems`, tool-role branch](https://github.com/dotnet/extensions/blob/v10.11.0/src/Libraries/Microsoft.Extensions.AI.OpenAI/OpenAIResponsesChatClient.cs).
Reload yielded `JsonElement` for every object-typed `Result`, bypassing the original
string/content-block paths. Non-result native prefix, instructions, flags and
call/result identities stayed unchanged. Comparing decoded text would hide the
text drift; comparing only initial definitions would miss every case.

**Raw JSON alone is not a universal repair either.** The pinned SDK's
`ModelReaderWriter.Read<ResponseItem>` rejected array-valued function output,
although the adapter writes such arrays using `FunctionCallOutputResponseItem.Patch`.
The replay control used ordinary native decoding for strings and that same patch
escape hatch for the array. It then preserved the complete prior input prefix:
ordered JSON structure and exact string/array values, **not byte-identical HTTP**.
Captures supplied the oracle; this does not qualify a production recorder/store.

### Runs and limits

Final **`20261104-105955-039`: 22 assertions passed, three local requests**, clean
build. Initial `20261104-105731-870` completed the before/reload comparisons but
stopped at the SDK array-reader limitation before request three; the reviewed
revision made that limitation an explicit negative control. This was one fixture,
not a new test programme. Independent method/scope review preceded execution and
reviewed the narrowed replay correction. Final source/capture/log/report review
found no material issue; the reviewer did not rerun the fixture.

Evidence: `%TEMP%/urd-foundation-streams-6268b517/context-lifetime-e8222969/` contains
`LEDGER.md`, source/pins, `verify.ps1`, logs, `runs/*/request-*.json`, SSE scripts and
`comparison.json`. Restore used the retained adapter package cache as a read-only
local feed and a fixture-owned destination; no download/global installation.
Pre-build free RAM was ~2 GiB. Both runs held a started `urd:heavy-local-work` lease;
owned fixture processes stopped before release. Reproduction requires acquiring
and starting that lease, then `verify.ps1 -LeaseId <lease>`.

No real model consumed these requests. No live-provider acceptance, image
interpretation, cache-hit/billing, process-restart, production-storage or other-OS
claim follows. Synthetic image bytes were deliberately not an actual image.
Original readable history, permissions, pruning and reset behavior were researched,
not implemented or runtime-qualified by this fixture.

## Recommendation and tomorrow's judgment

**Engineering recommendation:** make exact already-sent result reconstruction an
acceptance condition alongside native IDs/order. Do not blindly serialize common
interface objects or call decoded equivalence prefix preservation. Keep direct
`IChatClient`; qualify its adapters at the actual durable-context boundary.

Two meaningful implementation options remain: explicitly reconstruct supported
common result types (less provider coupling, requires complete type/fidelity handling),
or retain the needed first-sent native result representation (stronger route fidelity,
provider-specific decoding/version work). The replay control proves a path, not the
choice. Do not retain arbitrary HTTP envelopes/credentials or duplicate all history.

**Human judgment:** how explicit model/provider/guidance changes take effect, and
when compaction/refresh should rerun selection. Recommend discussing continuity
versus deliberately refreshed capabilities separately: retaining selection avoids
surprise/cost; reselection may suit changed tasks/models but changes exposure.
Pie's retained-selection behavior is a meaningful starting alternative, not approval.
Uncertain native portability must not become silent content loss. Codec fields and
SQL are engineering details, not owner votes.

**Smallest next qualification:** when the actual context owner exists, repeat this
already-sent-result invariant through its supported text/structured/multimodal
representation, including legitimate new-result reduction. Separately qualify one
chosen reset transition without losing readable originals or current restrictions.
Live native/wrapper reliability and cache economics still require authorized
provider/model evidence; none is authorized by this report.
