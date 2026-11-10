# Transcript whole-history position probe

**2026-11-10.** A synthetic SQLite probe supports the flat weighted-position lookup
as the initial baseline; it does not select production schema or qualify end-to-end
navigation. [Transcript design](TRANSCRIPT-DESIGN.md) retains the open gates.

## Findings

The fixture held 100,000 entries in each of five interleaved sessions (500,000
total), plus an empty session. Fresh position lookup including database open had
p50/p95/max **1.129/1.540/3.854 ms**; fresh prompt lookup was
**1.143/1.486/2.789 ms**. Warm p50/p95/max were **0.203/0.366/0.494 ms** for
position and **0.170/0.358/2.181 ms** for prompts. Fresh connections did not flush
the OS cache; this is backend lookup, not derived-cold user interaction. Queries
used indexed weighted seeks, exact identity resolution and a bounded 60-entry
keyset page, without body reads, OFFSET or repeated whole-history scans.

The flat projection added **21.64 MB** (43.28 bytes/entry; 7.86% of baseline
database). Five-entry append median increased **0.194 to 0.378 ms** (one serial,
non-randomized comparison); direct active-tail weight update median was **0.045 ms**.
Historical corrections rewrote suffixes: median **227 ms** at the first row,
**121 ms** at halfway, and **29 ms** at 90%, with **7–15 MB WAL per early/mid
correction**. Therefore, do not add a hierarchy for lookup speed, but historical
maintenance remains an unresolved gate: if frequent old-weight changes are required,
probe batching/replacement or another summary strategy before selection. Do not let
browser width-dependent measurements trigger database suffix rewrites.

All **590,786** correctness assertions passed, including weighted target oracle,
prompt identity, interleaved bounded pages, empty and large-body cases, revision
fencing, append/tail/historical maintenance, rollback and SQLite integrity checks.
No concurrent revision/append race or crash-during-commit case was tested. Storage
measurements are synthetic and serial; they do not establish contention or durability
behavior.

## Evidence

Raw report and executable records: `%TEMP%/urd-position-probe-5ba1aec72fc9/REPORT.md`
and its neighboring `results.json`, `run.log`, fixture and review files. Fresh cache
means a new SQLite connection, not a cold disk. Pixel geometry, rendering, transport,
giant-entry subranges, browser eviction and composed Stage 2 behavior remain outside
this probe.
