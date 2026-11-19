import assert from "node:assert/strict";
import type { Locator, Page } from "playwright";
import {
  browserCase,
  scrollBy,
  settle,
  until,
  visibleOffset,
} from "../../support/browser/BrowserTest.ts";

const positionRead = (url: URL) => url.pathname.endsWith("/positions/locate");
const olderRead = (url: URL) =>
  positionRead(url) && Number(url.searchParams.get("fraction")) < 0.97;

function message(port: Locator, number: number) {
  return port.getByText(`Message ${String(number).padStart(3, "0")}`, { exact: true });
}

async function expectMessage(port: Locator, number: number) {
  await until(
    `message ${number} intersects the scrollport`,
    async () => (await visibleOffset(message(port, number), port)) !== null,
  );
}

async function firstSource(port: Locator) {
  return port.evaluate((node) => {
    const port = node.getBoundingClientRect();
    for (const piece of node.querySelectorAll<HTMLElement>("[data-piece]")) {
      const rect = piece.getBoundingClientRect();
      if (rect.bottom > port.top && rect.top < port.bottom && piece.textContent)
        return { label: piece.textContent, offset: rect.top - port.top };
    }
    return null;
  });
}

type Source = NonNullable<Awaited<ReturnType<typeof firstSource>>>;

async function readingSource(page: Page, port: Locator): Promise<Source> {
  await settle(page);
  let source: Source | null = null;
  await until(
    "viewport contains readable source",
    async () => (source = await firstSource(port)) !== null,
  );
  assert(source);
  return source;
}

async function expectAnchor(port: Locator, source: Source, tolerance = 24) {
  const offset = await visibleOffset(port.getByText(source.label, { exact: true }), port);
  assert(offset !== null, `${source.label} no longer visible (was at ${source.offset})`);
  assert(
    Math.abs(offset - source.offset) <= tolerance,
    `${source.label} moved from ${source.offset} to ${offset} (tolerance ${tolerance})`,
  );
}

async function startReading(page: Page, port: Locator) {
  await expectMessage(port, 320);
  await settle(page);
  await scrollBy(port, -350);
  return readingSource(page, port);
}

async function publish(page: Page) {
  await page.getByRole("button", { name: "Publish change notice" }).click();
}

async function seekFraction(port: Locator, fraction: number) {
  await port.evaluate((node, fraction) => {
    node.scrollTop = (node.scrollHeight - node.clientHeight) * fraction;
  }, fraction);
}

browserCase(
  "1: opens at the latest message without mounting history",
  async ({ port, fixture }) => {
    await expectMessage(port, 320);
    assert.equal(await message(port, 1).count(), 0);
    const count = await port.locator("[data-transcript-entry]").count();
    assert(count > 0 && count <= 30, `expected a bounded DOM, found ${count} rows`);
    const reads = fixture.requests.filter(
      (request) => request.method === "GET" && request.url.includes("positions/locate"),
    );
    assert.equal(reads.length, 1);
    assert.equal(new URL(reads[0]!.url, fixture.origin).searchParams.get("fraction"), "1");
  },
);

browserCase(
  "2: reading stays anchored when the older window arrives",
  async ({ page, port, fixture }) => {
    await startReading(page, port);
    const held = fixture.hold(olderRead);
    try {
      await scrollBy(port, -890);
      await held.received;
      const anchor = await readingSource(page, port);
      const oldRank = await port
        .locator("[data-entry-rank]")
        .first()
        .getAttribute("data-entry-rank");
      held.release();
      await until("an earlier row mounts after the window response", async () => {
        const first = await port
          .locator("[data-entry-rank]")
          .first()
          .getAttribute("data-entry-rank");
        return first !== null && Number(first) < Number(oldRank);
      });
      await settle(page);
      await expectAnchor(port, anchor);
    } finally {
      held.release();
    }
  },
);

browserCase(
  "3: append preserves reading; append while following reveals the tail",
  async ({ page, port, fixture }) => {
    const anchor = await startReading(page, port);
    const updated = fixture.hold(positionRead);
    try {
      fixture.append();
      await publish(page);
      await updated.received;
      updated.release();
      await until(
        "append window finished loading",
        async () => (await port.getAttribute("aria-busy")) === "false",
      );
      await settle(page);
      await expectAnchor(port, anchor);
      assert.equal(await visibleOffset(message(port, 321), port), null);
    } finally {
      updated.release();
    }
    await seekFraction(port, 1);
    await expectMessage(port, 321);
    fixture.append();
    await publish(page);
    await expectMessage(port, 322);
  },
);

browserCase(
  "4: an evicted tab restores its reading source, then respects user scroll",
  async ({ page, port, fixture }) => {
    const anchor = await startReading(page, port);
    await page.getByRole("button", { name: "Other tab" }).click();
    await until("original conversation has unmounted", async () => (await port.count()) === 0);
    await page.getByRole("button", { name: "Conversation tab" }).click();
    await until(
      "return restores the previously visible source",
      async () =>
        (await visibleOffset(port.getByText(anchor.label, { exact: true }), port)) !== null,
    );
    await settle(page);
    await expectAnchor(port, anchor);
    assert(
      fixture.requests.some((request) => request.url.includes("/positions/entries/")),
      "return resolves an entry anchor instead of opening at the tail",
    );
    await scrollBy(port, -230);
    const next = await readingSource(page, port);
    assert.notEqual(next.label, anchor.label);
    await settle(page);
    await expectAnchor(port, next);
  },
);

browserCase("5: width change retains the reading source offset", async ({ page, port }) => {
  const anchor = await startReading(page, port);
  const before = await port.evaluate((element) => element.clientWidth);
  await page.setViewportSize({ width: 465, height: 560 });
  await until(
    "scrollport narrows",
    async () => (await port.evaluate((element) => element.clientWidth)) < before,
  );
  await settle(page);
  await expectAnchor(port, anchor, 30);
});

browserCase("6: Jump to latest re-enables following", async ({ page, port, fixture }) => {
  await startReading(page, port);
  const jump = page.getByRole("button", { name: "Jump to latest" });
  await jump.click();
  await expectMessage(port, 320);
  await until("jump control disappears while following", async () => (await jump.count()) === 0);
  fixture.append();
  await publish(page);
  await expectMessage(port, 321);
});

browserCase(
  "7: native selection survives a distant scroll and append",
  async ({ page, port, fixture }) => {
    const anchor = await startReading(page, port);
    const selected = port.getByText(anchor.label, { exact: true });
    await selected.evaluate((node) => {
      const range = document.createRange();
      range.selectNodeContents(node);
      const selection = document.getSelection()!;
      selection.removeAllRanges();
      selection.addRange(range);
    });
    await settle(page);
    await seekFraction(port, 0.5);
    await until("distant history is visible", async () => {
      const source = await firstSource(port);
      return /^Message 1[4-8]\d$/.test(source?.label ?? "");
    });
    const updated = fixture.hold(positionRead);
    try {
      fixture.append();
      await publish(page);
      await updated.received;
      updated.release();
      await until(
        "append window published",
        async () => (await port.getAttribute("aria-busy")) === "false",
      );
      await settle(page);
      assert.equal(await visibleOffset(selected, port), null);
      assert.equal(await page.evaluate(() => document.getSelection()?.toString()), anchor.label);
    } finally {
      updated.release();
    }
  },
);

browserCase(
  "8: obsolete older response cannot replace newer navigation; Retry recovers",
  async ({ page, port, fixture }) => {
    await expectMessage(port, 320);
    // Simulate a transport that finishes an obsolete read despite cancellation.
    // The real client, reader, and transcript must still fence publication.
    await page.evaluate(() => {
      const fetch = window.fetch.bind(window);
      window.fetch = (input, options) => {
        const url = new URL(input instanceof Request ? input.url : String(input), location.href);
        const fraction = url.searchParams.get("fraction");
        return url.pathname.endsWith("/positions/locate") && fraction && Number(fraction) < 0.3
          ? fetch(input, { ...options, signal: null })
          : fetch(input, options);
      };
    });
    const old = fixture.hold(olderRead);
    try {
      await seekFraction(port, 0.2);
      await old.received;
      await seekFraction(port, 0.66);
      await until("second seek is populated first", async () =>
        /^Message 2[01]\d$/.test((await firstSource(port))?.label ?? ""),
      );
      const anchor = await readingSource(page, port);
      const delivered = page.waitForResponse((response) => {
        const url = new URL(response.url());
        return (
          response.request().method() === "GET" &&
          olderRead(url) &&
          Number(url.searchParams.get("fraction")) < 0.3
        );
      });
      old.release();
      const response = await delivered;
      assert(response.ok());
      await response.finished();
      await settle(page);
      await expectAnchor(port, anchor);
    } finally {
      old.release();
    }

    const failed = fixture.hold(olderRead, true);
    try {
      await seekFraction(port, 0.4);
      await failed.received;
      failed.release();
      const retry = page.getByRole("button", { name: "Retry", exact: true });
      await until("failed read exposes Retry", async () => (await retry.count()) === 1);
      // The error lives at the transcript's head, which is outside this seek viewport.
      // A programmatic click avoids scrolling that button into view and starting another seek.
      await retry.evaluate((button) => (button as HTMLButtonElement).click());
      await until("retry displays content from the failed destination", async () =>
        /^Message 12\d$/.test((await firstSource(port))?.label ?? ""),
      );
      assert.equal(await retry.count(), 0);
    } finally {
      failed.release();
    }
  },
);
