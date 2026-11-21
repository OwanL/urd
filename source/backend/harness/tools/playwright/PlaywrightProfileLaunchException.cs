namespace Urd.Core.Tools;

// The launcher could not confirm that the browser released the profile.
internal sealed class PlaywrightProfileLaunchException(Exception cause)
  : InvalidOperationException("Could not confirm that Chromium released the browser profile.", cause);
