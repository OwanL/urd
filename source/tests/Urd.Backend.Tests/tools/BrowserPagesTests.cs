using Urd.Core.Tools;
using Xunit;

namespace Urd.Backend.Tests.Tools;

public sealed class BrowserPagesTests {
  [Fact]
  public void PagesAreScopedToTheirOwner() {
    var pages = new BrowserPages<object>();
    var first = Guid.NewGuid();
    var second = Guid.NewGuid();
    var page = pages.Register(first, new object()).Page;
    var other = pages.Register(second, new object()).Page;

    Assert.NotEqual(page.Id, other.Id);
    Assert.Equal(first, page.OwnerId);
    Assert.Null(pages.Get(second, page.Id));
    Assert.False(pages.Select(second, page.Id));
    Assert.False(pages.Touch(second, page.Id));
    Assert.Null(pages.Remove(second, page.Id));
    Assert.Throws<ArgumentException>(() => pages.Register(second, new object(), page.Id));
    Assert.Single(pages.List(first));
    Assert.Equal(other.Id, pages.Selected(second)!.Id);
  }

  [Fact]
  public void PopupEvictsLeastRecentlyUsedPageExceptItsOpener() {
    var pages = new BrowserPages<object>();
    var owner = Guid.NewGuid();
    var opener = pages.Register(owner, new object()).Page;
    var victim = pages.Register(owner, new object()).Page;
    var remaining = pages.Register(owner, new object()).Page;

    var popup = pages.Register(owner, new object(), opener.Id);

    Assert.Equal(victim.Id, popup.Evicted!.Id);
    Assert.Null(pages.Get(owner, victim.Id));
    Assert.Equal(new[] { opener.Id, remaining.Id, popup.Page.Id }, pages.List(owner).Select(page => page.Id));
    Assert.Equal(popup.Page.Id, pages.Selected(owner)!.Id);
  }

  [Fact]
  public void ViewerTouchDoesNotChangeSelectionAndRemovalSelectsMostRecentSurvivor() {
    var pages = new BrowserPages<object>();
    var owner = Guid.NewGuid();
    var first = pages.Register(owner, new object()).Page;
    var second = pages.Register(owner, new object()).Page;
    var third = pages.Register(owner, new object()).Page;
    Assert.True(pages.Select(owner, second.Id));
    Assert.True(pages.Touch(owner, first.Id));
    Assert.Equal(second.Id, pages.Selected(owner)!.Id);

    Assert.Equal(second.Id, pages.Remove(owner, second.Id)!.Id);
    Assert.Equal(first.Id, pages.Selected(owner)!.Id);
    Assert.NotNull(pages.Remove(owner, third.Id));
    Assert.Equal(first.Id, pages.Selected(owner)!.Id);
    Assert.NotNull(pages.Remove(owner, first.Id));
    Assert.Null(pages.Selected(owner));
    Assert.Empty(pages.List(owner));
  }
}
