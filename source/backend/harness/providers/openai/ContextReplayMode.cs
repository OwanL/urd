namespace Urd.Core.Providers;

// Selects stored-message restoration, same-scope native replay or cross-scope portable export.
internal enum ContextReplayMode { Restore, NativeReplay, PortableExport }
