import assert from "node:assert/strict";
import type { Locator } from "playwright";
import { browserCase, settle, until, visibleOffset } from "../support/browser/BrowserTest.ts";

// Position the viewport inside an unmaterialized root gap, not on a rendered row.
async function seekGap(port: Locator, piece: number) {
  const gap = port.locator('[data-markdown-gap="root"]').filter({ visible: true });
  const target = await gap.evaluateAll((nodes, index) => {
    const node = nodes.find((element) => {
      const from = Number((element as HTMLElement).dataset.gapFrom);
      const next = Number((element as HTMLElement).dataset.gapNext);
      return from <= index && index < next;
    }) as HTMLElement | undefined;
    if (!node) return null;
    const from = Number(node.dataset.gapFrom);
    const next = Number(node.dataset.gapNext);
    const rect = node.getBoundingClientRect();
    return rect.top + (rect.height * (index + 0.5 - from)) / (next - from);
  }, piece);
  assert(target !== null, `piece ${piece} must start in a root gap`);
  await port.evaluate((element, y) => {
    const rect = element.getBoundingClientRect();
    element.scrollTop += y - rect.top - element.clientHeight / 2;
    element.dispatchEvent(new Event("scroll"));
  }, target);
}

async function bounded(port: Locator) {
  const count = await port.locator("[data-piece]").count();
  assert.ok(count > 0 && count < 300, `rendered piece count ${count} should remain bounded`);
}

browserCase(
  "long Markdown seeks into gaps and back without losing semantics",
  async ({ page, port }) => {
    await until(
      "long Markdown root gap",
      async () => (await port.locator('[data-markdown-gap="root"]').count()) > 0,
    );
    await seekGap(port, 450);
    await until("middle Markdown rows become visible", async () => {
      const ids = await port.evaluate((element) => {
        const scrollport = element.getBoundingClientRect();
        return [...element.querySelectorAll<HTMLElement>("[data-piece]")]
          .filter((node) => {
            const rect = node.getBoundingClientRect();
            return rect.bottom > scrollport.top && rect.top < scrollport.bottom;
          })
          .map((node) => Number(node.dataset.piece));
      });
      return ids.some((id) => id > 300 && id < 600);
    });
    await bounded(port);

    // The end contains both a multi-line code block and a semantic table.
    await port.evaluate((element) => {
      element.scrollTop = element.scrollHeight;
      element.dispatchEvent(new Event("scroll"));
    });
    await until(
      "end table is rendered",
      async () => (await port.getByRole("columnheader", { name: "Feature" }).count()) === 1,
    );
    assert.equal(await port.locator("table").count(), 1);
    assert.equal(await port.locator("table tr").count(), 4);
    assert.equal(await port.locator("table th").count(), 3);
    assert.equal(await port.locator("table td").count(), 9);
    assert.equal(await port.locator("pre code").count(), 1);
    assert.match(await port.locator("pre code").innerText(), /Code line 01[\s\S]*Code line 12/);
    await bounded(port);

    await seekGap(port, 100);
    await until("early Markdown rows return", async () => {
      const row = port.getByText(/^Markdown row (0\d\d|1\d\d)$/);
      const count = await row.count();
      for (let i = 0; i < count; i++)
        if ((await visibleOffset(row.nth(i), port)) !== null) return true;
      return false;
    });
    await bounded(port);
    await settle(page);
  },
  "markdown",
);

browserCase(
  "loading a nearby managed image preserves the paragraph in view",
  async ({ page, fixture, port }) => {
    // Reload after installing the hold so even a fast initial image request cannot escape it.
    const held = fixture.hold((url) => url.pathname.includes("/markdown-images/"));
    try {
      await page.reload({ waitUntil: "domcontentloaded" });
      await until(
        "image session mounted",
        async () => (await port.locator(".markdown-renderer").count()) > 0,
      );
      await port.evaluate((element) => {
        element.scrollTop = 0;
        element.dispatchEvent(new Event("scroll"));
      });
      await held.received;
      const paragraph = port.getByText("Image paragraph 010", { exact: true });
      await until(
        "paragraph near pending image is rendered",
        async () => (await paragraph.count()) === 1,
      );
      await paragraph.evaluate((element) => element.scrollIntoView({ block: "start" }));
      await settle(page);
      const before = await visibleOffset(paragraph, port);
      assert(before !== null, "paragraph must be visible while image is held");
      const placeholder = port.locator(".markdown-renderer-image").first();
      const placeholderBounds = await placeholder.boundingBox();
      const portBounds = await port.boundingBox();
      assert(placeholderBounds && portBounds);
      assert.ok(
        placeholderBounds.y + placeholderBounds.height <= portBounds.y,
        "pending image must be above the reading position",
      );
      assert.ok((await port.evaluate((element) => element.scrollTop)) > 0);
      const placeholderHeight = placeholderBounds.height;
      held.release();
      const image = port.getByRole("img", { name: "Delayed managed image" });
      await until(
        "managed image loads",
        async () =>
          (await image.count()) === 1 &&
          (await image.evaluate(
            (element) =>
              (element as HTMLImageElement).complete &&
              (element as HTMLImageElement).naturalWidth > 0,
          )),
      );
      await settle(page);
      const loadedHeight = await image.evaluate(
        (element) => element.getBoundingClientRect().height,
      );
      assert.ok(loadedHeight > placeholderHeight + 20, "image load must change layout geometry");
      const after = await visibleOffset(paragraph, port);
      assert(after !== null, "paragraph must remain visible after image loads");
      assert.ok(
        Math.abs(after - before) < 40,
        `paragraph moved ${after - before}px after image load`,
      );
    } finally {
      held.release();
    }
  },
  "image",
);
