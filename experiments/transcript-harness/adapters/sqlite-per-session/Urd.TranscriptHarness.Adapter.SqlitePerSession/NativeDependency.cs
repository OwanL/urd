using System.Reflection;
using Urd.TranscriptHarness.Adapter.SqliteRows;

namespace Urd.TranscriptHarness.Adapter.SqlitePerSession;

// SQLitePCLRaw battery/provider wiring for this adapter.
//
// The provider assembly (SQLitePCLRaw.provider.e_sqlite3) accepts exactly ONE
// NativeLibrary.SetDllImportResolver registration per process, and the
// composed rows adapter (project-referenced, read-only, shared) registers such
// a resolver unconditionally in its own NativeDependency. This adapter ships
// NO competing wiring: it triggers the rows adapter's wiring (managed deps
// resolution from that adapter's own .deps.json, native resolution of
// "e_sqlite3", explicit Batteries_V2 provider init) through one contained
// reflection call before opening any connection. The rows wiring is
// idempotent (its own static gate), so later rows.Open() calls are no-ops and
// no "resolver already set" failure can occur in this adapter's process —
// whether standalone (tests runner) or host-loaded (shared smoke/validate).
//
// If the rows adapter is not present, Open() fails loudly at wiring time
// (composition with it is a declared family-8 requirement).

internal static class NativeDependency
{
    private static bool _wired;

    public static void EnsureResolved()
    {
        if (_wired) return;
        var rowsAssembly = typeof(SqliteRowsTranscriptStore).Assembly;
        var rowsNativeDependency = rowsAssembly.GetType(
            "Urd.TranscriptHarness.Adapter.SqliteRows.NativeDependency",
            throwOnError: true)!;
        rowsNativeDependency
            .GetMethod("EnsureResolved", BindingFlags.Public | BindingFlags.Static)
            ?.Invoke(null, null);
        _wired = true;
    }
}