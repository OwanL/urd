import assert from "node:assert/strict";
import test from "node:test";
import { chromium } from "playwright";
import { startLocalModelServer } from "./LocalModelServer.ts";
import { ServerProcess } from "./ServerProcess.ts";

void test("offline browser keeps drafts across reload and sends through the local fixture", async () => {
  const model = await startLocalModelServer();
  let server: ServerProcess | undefined;
  let browser: Awaited<ReturnType<typeof chromium.launch>> | undefined;
  try {
    server = await ServerProcess.start();
    await model.install(server.origin);
    browser = await chromium.launch({ headless: true });
    const page = await browser.newPage();
    const pageErrors: string[] = [];
    let messageSubmissions = 0;
    page.on("pageerror", (error) => pageErrors.push(error.message));
    await page.route("**/api/sessions/*/message-submissions", async (route) => {
      if (route.request().method() === "POST") messageSubmissions++;
      await route.continue();
    });

    await page.goto(server.origin.href);
    const message = page.getByRole("textbox", { name: "Message" });
    await message.waitFor();
    const modelPicker = page.getByRole("button", { name: /^Model:/ });
    await modelPicker.click();
    const fixtureModel = page.getByRole("option", { name: "fixture-a" });
    await fixtureModel.waitFor();
    await fixtureModel.click();

    const send = page.getByRole("button", { name: "Send" });
    await message.fill("draft survives reload");
    await page.waitForFunction(() =>
      Object.keys(sessionStorage).some((key) => key.startsWith("urd.workspace.v1.draft.")),
    );
    await page.reload();
    await message.waitFor();
    assert.equal(await message.inputValue(), "draft survives reload");

    model.enqueue({ text: "offline fixture reply" });
    await page.waitForFunction(() => {
      const button = document.querySelector<HTMLButtonElement>('button[aria-label="Send"]');
      return button !== null && !button.disabled;
    });
    await send.click();
    await page.getByText("offline fixture reply").waitFor();
    await page.waitForFunction(
      () => !Object.keys(sessionStorage).some((key) => key.startsWith("urd.workspace.v1.draft.")),
    );
    assert.equal(messageSubmissions, 1);
    assert.equal(model.calls.length, 1);
    assert.equal(model.calls[0]?.request.model, "fixture-a");

    await page.evaluate(() => {
      const original = Storage.prototype.setItem;
      let failed = false;
      Storage.prototype.setItem = function (key, value) {
        if (!failed && key.startsWith("urd.workspace.v1.draft.")) {
          failed = true;
          throw new DOMException("Temporary storage failure", "QuotaExceededError");
        }
        original.call(this, key, value);
      };
      Object.defineProperty(window, "restoreSessionStorage", {
        value: () => {
          Storage.prototype.setItem = original;
        },
      });
    });
    await message.fill("draft after storage recovery");
    await page.getByRole("alert").getByRole("button", { name: "Retry save" }).waitFor();
    assert.equal(await send.isDisabled(), true);
    await page.evaluate(() => {
      (window as unknown as { restoreSessionStorage: () => void }).restoreSessionStorage();
    });
    await page.getByRole("button", { name: "Retry save" }).click();
    await page.waitForFunction(() => {
      const button = document.querySelector<HTMLButtonElement>('button[aria-label="Send"]');
      return button !== null && !button.disabled;
    });
    assert.equal(await message.inputValue(), "draft after storage recovery");
    await page.reload();
    await message.waitFor();
    assert.equal(await message.inputValue(), "draft after storage recovery");
    assert.equal(messageSubmissions, 1);
    assert.equal(model.calls.length, 1);
    assert.deepEqual(pageErrors, []);
  } finally {
    await browser?.close();
    try {
      await server?.stop();
    } finally {
      await model.close();
    }
  }
});
