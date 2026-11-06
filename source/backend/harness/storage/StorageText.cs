using System.Text;

namespace Urd.Core.Storage;

// Shares the text encoding and bounded segment contract across storage projections.
internal static class StorageText {
    internal const int MaximumBytes = 16 * 1024;
    internal const int MaximumSegments = 16;
    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);
}
