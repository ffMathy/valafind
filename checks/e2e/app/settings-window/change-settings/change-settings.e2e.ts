// ail-derived-from: ChangeSettings@319a1d95e7796c33
// End-to-end check for ChangeSettings: its scenarios, played in the settings window of the running Valafind. It sees
// only what a user sees: windows on screen and their words (OCR), the keys that open the launcher, the autostart entry
// in the home folder, and what would have opened (see app-driver.ts). Every scenario that saves puts things back.
import { afterEach, expect } from "bun:test";
import { existsSync, mkdirSync, readFileSync, rmSync } from "node:fs";
import { join } from "node:path";
import { closeLauncher, home, keepScreen, launcher, opened, readScreen, sleep, tap, test, unique, waitFor, windows } from "../../../desktop.ts";
import {
  autostartEntry, chooseFirstFolderInHome, clickAtSettingsEnd, clickInSettings, closeSettings, escape, moveSlider,
  openSettings, pickFromDropDown, pressInShortcutBox, scrollSettingsToEnd, settingsWindows, waitForSettings,
} from "../../app-driver.ts";

afterEach(async () => {
  await escape();
  await closeSettings();
  await closeLauncher();
});

const settingsText = () => readScreen(windows("^Valafind Settings$")[0] ?? null);

/** Whether a chord opens the launcher; closes it again. */
async function opensLauncher(keys: string): Promise<boolean> {
  await closeLauncher();
  await tap(keys);
  const open = (await waitFor(launcher, 3000)) !== null;
  return open;
}

async function setShortcutToDefault() {
  await openSettings();
  await clickInSettings("reset");
  await clickInSettings("save");
  await waitForSettings(false);
}

test("Save with a new shortcut moves the launcher to the new keys", async () => {
  try {
    await openSettings();
    await pressInShortcutBox("ctrl+alt+k");
    await clickInSettings("save");
    expect(await waitForSettings(false)).toBe(true);
    expect(await opensLauncher("ctrl+alt+k")).toBe(true);
    await sleep(500);
    expect(readScreen()).toMatch(/Ctr[li1|]\+Alt\+K/);
    keepScreen();
    expect(await opensLauncher("ctrl+alt+space")).toBe(false);
  } finally {
    await closeLauncher();
    await setShortcutToDefault();
  }
  expect(await opensLauncher("ctrl+alt+space")).toBe(true);
}, 60_000);

test("a shortcut that cannot be used keeps the window open with the reason", async () => {
  await openSettings();
  await pressInShortcutBox("ctrl+Escape");
  await clickInSettings("save");
  await sleep(800);
  expect(settingsWindows().length).toBe(1);
  expect(settingsText()).toContain("Escape cannot be used as a global shortcut key.");
  keepScreen();
  await closeSettings();
  expect(await opensLauncher("ctrl+alt+space")).toBe(true);
}, 40_000);

test("Reset puts the default shortcut back", async () => {
  await openSettings();
  await pressInShortcutBox("ctrl+alt+k");
  expect(settingsText()).toContain("Ctrl+Alt+K");
  await clickInSettings("reset");
  await sleep(300);
  const text = settingsText();
  expect(text).not.toContain("Ctrl+Alt+K");
  expect(text).toContain("Ctrl+Alt+Space");
  keepScreen();
}, 30_000);

test("Esc forgets a changed limit", async () => {
  await openSettings();
  await moveSlider("cpu", 30, 2);
  expect(settingsText()).toMatch(/Maximum CPU 40%/);
  await escape();
  expect(await waitForSettings(false)).toBe(true);
  await openSettings();
  expect(settingsText()).toMatch(/Maximum CPU 30%/);
  keepScreen();
}, 40_000);

test("Save keeps a changed limit", async () => {
  try {
    await openSettings();
    await moveSlider("disk", 25, 2);
    await clickInSettings("save");
    expect(await waitForSettings(false)).toBe(true);
    await openSettings();
    expect(settingsText()).toMatch(/Maximum disk busy 35%/);
    keepScreen();
  } finally {
    await closeSettings();
    await openSettings();
    if (/Maximum disk busy 35%/.test(settingsText())) {
      await moveSlider("disk", 35, -2);
      await clickInSettings("save");
      await waitForSettings(false);
    }
  }
}, 60_000);

test("adding a folder replaces the home folder in the list", async () => {
  const name = `AAApick${unique()}`; // sorts first among the home folder's folders, with capitals first or not
  mkdirSync(join(home(), name), { recursive: true });
  try {
    await openSettings();
    expect(settingsText()).toContain("Your home folder");
    await clickInSettings("addFolder");
    await chooseFirstFolderInHome();
    await waitForSettings(true);
    const listed = settingsText();
    expect(listed).toContain(name);
    expect(listed).toContain("Remove");
    expect(listed).not.toContain("Your home folder");
    keepScreen();
    await clickInSettings("removeFirstFolder");
    expect(settingsText()).toContain("Your home folder");
  } finally {
    await closeSettings();
    rmSync(join(home(), name), { recursive: true, force: true });
  }
}, 60_000);

test("choosing another language says it applies at the next start", async () => {
  await openSettings();
  await clickAtSettingsEnd("language");
  await pickFromDropDown("language", 12);
  expect(settingsText()).toContain("Takes effect the next time Valafind starts.");
  keepScreen();
}, 30_000);

test("choosing other dimensions warns that every file is indexed again", async () => {
  await openSettings();
  await clickAtSettingsEnd("dimensions");
  await pickFromDropDown("dimensions", 2);
  expect(settingsText()).toContain("Changing the dimensions re-indexes all files.");
  keepScreen();
}, 30_000);

test("Start Valafind when I log in adds an entry that starts it at sign-in", async () => {
  try {
    await openSettings();
    await clickAtSettingsEnd("startAtLogin");
    await clickInSettings("save");
    expect(await waitForSettings(false)).toBe(true);
    const entry = await waitFor(() => existsSync(autostartEntry()) && readFileSync(autostartEntry(), "utf8"), 3000);
    expect(entry).toContain("Name=Valafind");
    expect(entry).toMatch(/^Exec=.*"--background"$/m);
  } finally {
    await openSettings();
    await scrollSettingsToEnd();
    keepScreen();
    await clickAtSettingsEnd("startAtLogin");
    await clickInSettings("save");
    await waitForSettings(false);
  }
  expect(existsSync(autostartEntry())).toBe(false);
}, 60_000);

test("Open log folder opens the folder of the logs", async () => {
  const before = opened().length;
  await openSettings();
  await clickAtSettingsEnd("openLogFolder");
  keepScreen();
  expect(await waitFor(() => opened()[before], 5000)).toMatch(/\/logs$/);
}, 30_000);

test("the Check for updates button opens the releases page when this copy cannot update itself", async () => {
  const before = opened().length;
  await openSettings();
  await clickAtSettingsEnd("checkForUpdates");
  keepScreen();
  expect(await waitFor(() => opened()[before], 5000)).toBe("https://github.com/ffMathy/valafind/releases");
}, 30_000);
