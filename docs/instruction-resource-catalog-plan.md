# Incremental instruction-resource catalog

**Status: implemented in source.** This record describes current behavior and limits; it
is not a claim that every packaged platform has been verified. The catalog supplies paths
to the existing `InstructionResourceDiscovery`; it does not select resources or replace the
[pruning contract](pruning-plan.md).

## Scope and discovery

The session's working directory is the boundary, not its Git root. Discovery includes
matching resources in that directory and descendants, but not ancestors or sibling trees.
Git is not required: hidden, ignored and untracked files are included. Instruction filenames,
other-harness exclusions, skill parsing and scope are owned by the
[context guide](../source/backend/harness/context/README.md). Urd's packaged skills are
available independently of the working directory. Project and packaged same-name skills
remain candidates; when a project skill is selected it replaces the packaged same-name
entry, unless that packaged entry is protected.

The bundled scanner searches filenames only. It does not follow symbolic links or junctions;
project files are opened and checked against the actual opened path before their contents are
read. On Linux and macOS, mount-aware traversal excludes virtual and remote filesystems, and
unknown or ambiguous filesystem types fail closed. Windows rejects network roots and unsafe
reparse-point roots. Checked-in binaries cover Windows, macOS and Linux x64/arm64 (Linux musl
uses the corresponding Linux asset). Asset presence and source branches are not end-to-end
verification on every OS or architecture.

## Runtime ownership and cache

There is one catalog per backend `UrdStore`, shared by session preparation and the Settings
resource picker. It stores matching paths, not instruction text or skill frontmatter. The
actual cache path is the session database path plus `.instructions.sqlite`; for the normal
server database this is `<DataRoot>/urd.sqlite.instructions.sqlite`, beside but separate
from `<DataRoot>/urd.sqlite`. The catalog cache has its own versioned schema (currently 1)
and is rebuildable without resetting session data. A cache recognized as Urd's catalog is
rebuilt if its schema or integrity is incompatible. Nonempty foreign files are not deleted
and opening them fails; a fresh empty SQLite file can be initialized as the derived cache.

The index file remains across backend restarts, but cached rows alone do not prove freshness.
A requested tree is scanned and reconciled with disk after restart before it is treated as
current. During a process lifetime, roots with working watchers can serve cached paths;
file and directory create/delete/rename events update matching rows. Moving a populated
directory triggers a scan of its destination subtree. Text and skill metadata are read later
by discovery, so editing a file does not require rescanning paths and takes effect at the
next preparation boundary.

Production startup requests best-effort warming of the saved default working directory
when one exists. Warming failures are ignored; a later consumer retries and observes a
scan failure if it persists. Other working directories scan on demand. Concurrent callers
can share covered scans. Cancelling a caller cancels only its wait, not shared scan work.
Watchers and scanner processes are owned by the catalog and drained on backend shutdown.

### Fresh-scan fallback

A watcher is used only when coverage can be established safely. If it is unavailable (for
example, recursive watching cannot safely cover a tree with nested mounts), each request
performs a fresh scan instead of trusting persisted rows. Watcher errors, notification
overflow or lost coverage trigger a reconciliation scan before results are returned. A
failed or cancelled scan is not published as a ready partial result; scan/index errors reach
the requesting operation. A warm cache read is never used to claim that changes made while
Urd was stopped have been observed.

The derived cache location uses SQLite sidecars as needed. Rebuilding the catalog does not
remove or rewrite the session database. The catalog itself does not delete project files or
change saved session context; a new selection is activated only at the existing initial or
successful-compaction preparation boundary.

## Scanner and packaging

The scanner is **fd v10.3.0**, bundled from the checked-in release executables and MIT/Apache
license notices in `../source/vendor/fd/`. It is resolved from the backend's application
base directory under `fd/<platform>-<architecture>/fd` (or `fd.exe`), not from `PATH`; Linux
musl maps to the canonical Linux folder. The build target maps a runtime identifier to its
asset. A clean Windows x64 server publish contained one runnable fd binary and both license notices.
The scanner uses exact eligible basenames, case-insensitive matching, hidden
and ignored files, NUL-delimited output, and four threads. It limits traversal to the requested
root and applies platform-specific mount/link safety rules. See the
[fd asset README](../source/vendor/fd/README.md) for hashes and packaging details.

The executable implementation contains Windows, macOS and Linux branches, and assets for
x64/arm64 on those platforms. This document does **not** claim that published layouts,
watcher behavior, mount behavior or native executable invocation have been verified on every
RID. Mount-policy unit tests use parsed/synthetic mount data; they do not prove behavior on
every live filesystem. Do not infer all-platform acceptance from the asset table.

## Retained test coverage and evidence limits

Retained tests exercise hidden/ignored matching, mount-plan parsing and fail-closed malformed
mount data; catalog tests cover shared/overlapping roots, watcher updates, directory moves,
restart reconciliation, lost coverage, unavailable watchers, cancellation, cache rebuild and
protection against scanning through links. These cases establish the tested behavior only;
they are not an exhaustive OS matrix or a proof of discovery completeness on arbitrary
filesystems. The current implementation and tests do not establish pruning quality or a
winner over keep-all fallback.

The 2026-11-18 Windows measurements below are historical filename-scan experiments, not
release acceptance or cross-platform performance guarantees: two four-thread `fd` walks of
`C:\` took 121 s and 133 s for 1,902 raw filename matches; the paths-only cache was 444 KiB
and reopening/reading it took 5 ms. `C:\dev` took 36 s. FastFind's standard-provider
whole-volume SQLite index was still incomplete at the 8-minute cutoff (~829 MiB). These
measurements do not cover cold-boot guarantees, watcher cost, or pruning latency.
