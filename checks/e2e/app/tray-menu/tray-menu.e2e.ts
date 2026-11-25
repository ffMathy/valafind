// End-to-end check for TrayMenu: its scenarios, played with the tray icon of the running Valafind. It sees only what
// a user sees: windows on screen, their words (OCR), and what would have opened in the browser (see app-driver.ts).
import { afterEach, expect } from "bun:test";
import { closeLauncher, keepScreen, launcher, opened, readScreen, test, waitFor } from "../../desktop.ts";
import { chooseFromTrayMenu, clickTrayIcon, closeSettings, escape, openTrayMenu, settingsWindows, waitForSettings } from "../app-driver.ts";

afterEach(async () => {
  await escape();
  await closeSettings();
  await closeLauncher();
});

test("clicking the tray icon opens the launcher", async () => {
  await closeLauncher();
  await clickTrayIcon();
  expect(await waitFor(launcher, 5000)).toBeTruthy();
  keepScreen();
}, 20_000);

test("Open Valafind opens the launcher", async () => {
  await closeLauncher();
  await chooseFromTrayMenu("Open Valafind");
  expect(await waitFor(launcher, 5000)).toBeTruthy();
  keepScreen();
}, 20_000);

test("Pause indexing pauses until Resume indexing", async () => {
  try {
    await chooseFromTrayMenu("Pause indexing");
    await chooseFromTrayMenu("Open Valafind");
    await waitFor(launcher, 5000);
    const paused = await waitFor(() => { const s = readScreen(); return s.includes("Indexing paused") && s.includes("Paused by you") ? s : null; }, 8000, 500);
    expect(paused).toContain("Resume");
    await openTrayMenu();
    expect(readScreen(null)).toContain("Resume indexing");
    keepScreen();
    await escape();
  } finally {
    await chooseFromTrayMenu("Resume indexing");
  }
  await chooseFromTrayMenu("Open Valafind");
  await waitFor(launcher, 5000);
  const resumed = await waitFor(() => !readScreen().includes("Indexing paused"), 8000, 500);
  expect(resumed).toBe(true);
}, 60_000);

test("Settings… opens the settings, and only once", async () => {
  await chooseFromTrayMenu("Settings…");
  expect(await waitForSettings(true)).toBe(true);
  keepScreen();
  await chooseFromTrayMenu("Settings…");
  await waitForSettings(true);
  expect(settingsWindows().length).toBe(1);
}, 20_000);

test("Check for updates… opens the releases page when this copy cannot update itself", async () => {
  const before = opened().length;
  await chooseFromTrayMenu("Check for updates…");
  const link = await waitFor(() => opened()[before], 5000);
  expect(link).toBe("https://github.com/ffMathy/valafind/releases");
  expect(settingsWindows().length).toBe(0);
}, 20_000);
