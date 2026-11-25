// End-to-end check for ModelSetup: its scenarios, played on a desktop of its own with a Valafind started in a fresh
// home and data folder, from a build without the model. It sees only what a user sees: the words on that desktop
// (OCR). "The model is put into the folder the launcher names" is what running the install command does; the check
// links the model that is already on this machine into that folder instead of downloading it (see app-driver.ts).
import { expect } from "bun:test";
import { keepScreen, keepScreenIfNone, sleep, test } from "../../desktop.ts";
import { installModel, launcherOn, readDisplay, startOwnValafind, type OwnValafind } from "../app-driver.ts";

async function firstStartWithoutModel(): Promise<{ own: OwnValafind; seen: string }> {
  const own = await startOwnValafind();
  let seen = "";
  for (const until = Date.now() + 60_000; Date.now() < until; await sleep(1000)) {
    if (!launcherOn(own.display)) continue;
    seen = readDisplay(own.display);
    if (seen.includes("model not installed")) break;
  }
  return { own, seen };
}

test("without the model the launcher shows the command that installs it", async () => {
  const { own, seen } = await firstStartWithoutModel();
  try {
    keepScreen(own.display);
    expect(seen).toContain("EmbeddingGemma 2 model not installed");
    // OCR reads the grey boxes' text and the body imperfectly; these parts it reads reliably.
    expect(seen).toContain("This build of Valafind");
    expect(seen).toContain("Install it once");
    expect(seen).toMatch(/bash '\S*tools\/fetch-model\.sh'/);
    expect(seen).toContain(`${own.data}/models/embeddinggemma-2`);
    expect(seen).toContain("Check again");
    expect(seen).toContain("Model unavailable");
  } finally {
    keepScreenIfNone(own.display);
    await own.stop();
  }
}, 120_000);

test("the model is noticed without a restart", async () => {
  const { own, seen } = await firstStartWithoutModel();
  try {
    expect(seen).toContain("model not installed");
    installModel(own.data);
    let now = "";
    for (const until = Date.now() + 60_000; Date.now() < until; await sleep(1000)) {
      now = readDisplay(own.display);
      if (now.includes("Search your files by meaning")) break;
    }
    keepScreen(own.display);
    expect(now).toContain("Search your files by meaning");
    expect(now).not.toContain("model not installed");
    expect(own.process.exitCode).toBeNull();
  } finally {
    keepScreenIfNone(own.display);
    await own.stop();
  }
}, 150_000);
