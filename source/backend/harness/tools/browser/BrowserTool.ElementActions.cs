using System.Text.Json;
using Microsoft.Playwright;

namespace Urd.Core.Tools;

// Actions that drive an element by ref, the keyboard or a screenshot position on the active page.
internal sealed partial class BrowserTool {
  private async Task<ToolOutput> ClickAsync(Guid owner, JsonElement arguments, CancellationToken cancellation) {
    string @ref = Require(arguments, "ref");
    (BrowserPage<IPage> page, BrowserInteraction interaction) = Current(owner);
    host.Touch(owner, page.Id);
    if (interaction.PendingDialog is not null)
      return await ResultAsync(owner, [], cancellation).ConfigureAwait(false);
    return await RunActionAsync(owner, interaction, $"Clicked {@ref}.", () => page.Page.GetByRef(@ref)
        .ClickAsync(InputClickOptions()), cancellation, allowGrace: true).ConfigureAwait(false);
  }

  private async Task<ToolOutput> TypeAsync(Guid owner, JsonElement arguments, CancellationToken cancellation) {
    string @ref = Require(arguments, "ref");
    string text = Require(arguments, "text");
    bool submit = OptionalBool(arguments, "submit");
    (BrowserPage<IPage> page, BrowserInteraction interaction) = Current(owner);
    host.Touch(owner, page.Id);
    if (interaction.PendingDialog is not null)
      return await ResultAsync(owner, [], cancellation).ConfigureAwait(false);
    ILocator target = page.Page.GetByRef(@ref);
    async Task TypeIntoAsync() {
      await target.ClickAsync(InputClickOptions()).ConfigureAwait(false);
      await target.PressSequentiallyAsync(text).ConfigureAwait(false);
      if (submit)
        await target.PressAsync("Enter", InputPressOptions()).ConfigureAwait(false);
    }
    return await RunActionAsync(owner, interaction, $"Typed into {@ref}.{(submit ? " Submitted with Enter." : "")}",
        TypeIntoAsync, cancellation, allowGrace: true).ConfigureAwait(false);
  }

  private async Task<ToolOutput> FillFormAsync(Guid owner, JsonElement arguments, CancellationToken cancellation) {
    FormField[] fields = RequireForm(arguments);
    (BrowserPage<IPage> page, BrowserInteraction interaction) = Current(owner);
    host.Touch(owner, page.Id);
    if (interaction.PendingDialog is not null)
      return await ResultAsync(owner, [], cancellation).ConfigureAwait(false);
    async Task FillFieldsAsync() {
      foreach (FormField field in fields) {
        ILocator target = page.Page.GetByRef(field.Ref);
        switch (field.Kind) {
          case "text":
            await target.FillAsync(field.Value, new LocatorFillOptions { Timeout = ElementTimeoutMilliseconds })
                .ConfigureAwait(false);
            break;
          case "checkbox":
            await target.SetCheckedAsync(Ticked(field.Value), new LocatorSetCheckedOptions { Timeout = ElementTimeoutMilliseconds })
                .ConfigureAwait(false);
            break;
          default: // "select" is validated when the form is read.
            await target.SelectOptionAsync(field.Value, new LocatorSelectOptionOptions { Timeout = ElementTimeoutMilliseconds })
                .ConfigureAwait(false);
            break;
        }
      }
    }
    return await RunActionAsync(owner, interaction, $"Filled {fields.Length} form fields.", FillFieldsAsync, cancellation)
        .ConfigureAwait(false);
  }

  private static bool Ticked(string value) {
    if (!bool.TryParse(value, out bool ticked))
      throw new ArgumentException($"Checkbox values are true or false, not \"{value}\".");
    return ticked;
  }

  private async Task<ToolOutput> HoverAsync(Guid owner, JsonElement arguments, CancellationToken cancellation) {
    string @ref = Require(arguments, "ref");
    (BrowserPage<IPage> page, BrowserInteraction interaction) = Current(owner);
    host.Touch(owner, page.Id);
    if (interaction.PendingDialog is not null)
      return await ResultAsync(owner, [], cancellation).ConfigureAwait(false);
    return await RunActionAsync(owner, interaction, $"Hovered {@ref}.", () => page.Page.GetByRef(@ref)
        .HoverAsync(new LocatorHoverOptions { Timeout = ElementTimeoutMilliseconds }), cancellation).ConfigureAwait(false);
  }

  private async Task<ToolOutput> PressAsync(Guid owner, JsonElement arguments, CancellationToken cancellation) {
    string key = Require(arguments, "key");
    (BrowserPage<IPage> page, BrowserInteraction interaction) = Current(owner);
    host.Touch(owner, page.Id);
    if (interaction.PendingDialog is not null)
      return await ResultAsync(owner, [], cancellation).ConfigureAwait(false);
    return await RunActionAsync(owner, interaction, $"Pressed {key}.", () => page.Page.Keyboard.PressAsync(key),
        cancellation, allowGrace: true).ConfigureAwait(false);
  }

  private async Task<ToolOutput> SelectAsync(Guid owner, JsonElement arguments, CancellationToken cancellation) {
    string @ref = Require(arguments, "ref");
    string[] values = RequireStrings(arguments, "values");
    (BrowserPage<IPage> page, BrowserInteraction interaction) = Current(owner);
    host.Touch(owner, page.Id);
    if (interaction.PendingDialog is not null)
      return await ResultAsync(owner, [], cancellation).ConfigureAwait(false);
    return await RunActionAsync(owner, interaction, $"Selected {string.Join(", ", values)} in {@ref}.",
        () => page.Page.GetByRef(@ref).SelectOptionAsync(values, new() { Timeout = ElementTimeoutMilliseconds }),
        cancellation).ConfigureAwait(false);
  }

  private async Task<ToolOutput> ClickAtAsync(Guid owner, JsonElement arguments, CancellationToken cancellation) {
    int x = RequireInt(arguments, "x");
    int y = RequireInt(arguments, "y");
    (BrowserPage<IPage> page, BrowserInteraction interaction) = Current(owner);
    host.Touch(owner, page.Id);
    if (interaction.PendingDialog is not null)
      return await ResultAsync(owner, [], cancellation).ConfigureAwait(false);
    ScreenshotRecord? basis = interaction.LatestScreenshot;
    if (basis is null)
      throw new InvalidOperationException("Take a screenshot first; click_at needs coordinates from the latest screenshot.");
    if (x < 0 || x >= basis.Width || y < 0 || y >= basis.Height)
      throw new ArgumentException($"Coordinates must be inside the {basis.Width}x{basis.Height} screenshot.");
    // The epoch check and the click are atomic against resize and capture on the same page.
    return await RunActionAsync(owner, interaction, $"Clicked at {x},{y}.",
        () => interaction.ClickScreenshotAsync(basis, x, y, cancellation), cancellation, allowGrace: true)
        .ConfigureAwait(false);
  }

  private async Task<ToolOutput> UploadAsync(Guid owner, JsonElement arguments, CancellationToken cancellation) {
    string @ref = Require(arguments, "ref");
    string[] paths = RequireStrings(arguments, "paths");
    (BrowserPage<IPage> page, BrowserInteraction interaction) = Current(owner);
    host.Touch(owner, page.Id);
    if (interaction.PendingDialog is not null)
      return await ResultAsync(owner, [], cancellation).ConfigureAwait(false);
    string[] files = paths.Select(path => {
      string full = Path.GetFullPath(path);
      if (!File.Exists(full))
        throw new ArgumentException($"No file at \"{full}\".");
      return full;
    }).ToArray();
    return await RunActionAsync(owner, interaction, $"Uploaded {files.Length} file(s) into {@ref}.",
        () => page.Page.GetByRef(@ref).SetInputFilesAsync(files, new() { Timeout = ElementTimeoutMilliseconds }),
        cancellation).ConfigureAwait(false);
  }

  // The pinned Playwright still honors these flags despite the obsolete attributes. Let the
  // tool own the navigation wait instead of letting the input command time out before commit.
#pragma warning disable CS0612
  private static LocatorClickOptions InputClickOptions() => new() { Timeout = ElementTimeoutMilliseconds, NoWaitAfter = true };
  private static LocatorPressOptions InputPressOptions() => new() { Timeout = ElementTimeoutMilliseconds, NoWaitAfter = true };
#pragma warning restore CS0612
}
