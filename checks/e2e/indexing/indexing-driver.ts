// Helpers for the indexing checks. Like desktop.ts they only use what a user can see and do: files saved where a user
// saves them, the mouse and keys, and what is on the screen (OCR).
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
  type Box, closeLauncher, keepScreen, launcher, openLauncher, readScreen, search, sleep, tap, waitFor, windows, xdotool,
} from "../desktop.ts";

function run(cmd: string[]): string {
  return Bun.spawnSync(cmd, { env: process.env, stdout: "pipe", stderr: "pipe" }).stdout.toString();
}

/** What the launcher shows below its search box: the query typed in the box is left out, so a word found here was
 * found in the results, not in what was typed. */
export function resultsText(): string {
  const box = launcher();
  if (!box) return "";
  const below: Box = { ...box, y: box.y + 60, height: box.height - 60 };
  return readRegion(below);
}

/** OCR of a part of the screen (absolute coordinates). */
export function readRegion(region: Box, scale = 2): string {
  const dir = mkdtempSync(join(tmpdir(), "ail-ocr-"));
  const shot = join(dir, "shot.png"), big = join(dir, "big.png");
  run(["import", "-window", "root", "-crop", `${region.width}x${region.height}+${region.x}+${region.y}`, "+repage", shot]);
  run(["convert", shot, "-resize", `${scale * 100}%`, "-colorspace", "Gray", big]);
  const text = run(["tesseract", big, "stdout", "--psm", "6"]);
  rmSync(dir, { recursive: true, force: true });
  return text.replace(/[“”]/g, '"').replace(/[‘’]/g, "'").replace(/\s+/g, " ").trim();
}

type Word = { text: string; x: number; y: number; w: number; h: number; line: string };

/** Every word OCR finds in a region of the screen, with its position on the screen. */
export function wordsIn(region: Box | null = null, scale = 2): Word[] {
  const r = region ?? { id: "root", x: 0, y: 0, width: 1280, height: 800 };
  const dir = mkdtempSync(join(tmpdir(), "ail-ocr-"));
  const shot = join(dir, "shot.png"), big = join(dir, "big.png");
  run(["import", "-window", "root", "-crop", `${r.width}x${r.height}+${r.x}+${r.y}`, "+repage", shot]);
  run(["convert", shot, "-resize", `${scale * 100}%`, "-colorspace", "Gray", big]);
  const tsv = run(["tesseract", big, "stdout", "--psm", "11", "tsv"]);
  rmSync(dir, { recursive: true, force: true });
  const words: Word[] = [];
  for (const row of tsv.split("\n").slice(1)) {
    const c = row.split("\t");
    if (c.length < 12 || !c[11].trim() || Number(c[10]) < 0) continue;
    words.push({
      text: c[11].trim(), x: r.x + Number(c[6]) / scale, y: r.y + Number(c[7]) / scale,
      w: Number(c[8]) / scale, h: Number(c[9]) / scale, line: `${c[2]}.${c[3]}.${c[4]}`,
    });
  }
  return words;
}

/** The centre of the first place on screen where `phrase` is written (its words in a row), or null. */
export function find(phrase: string, region: Box | null = null): { x: number; y: number } | null {
  const want = phrase.toLowerCase().split(/\s+/);
  const words = wordsIn(region);
  for (let i = 0; i < words.length; i++) {
    let ok = true;
    for (let j = 0; j < want.length && ok; j++) {
      const w = words[i + j];
      ok = !!w && w.text.toLowerCase().replace(/[^a-z0-9%…]/g, "").startsWith(want[j].replace(/[^a-z0-9%…]/g, ""));
    }
    if (ok) {
      const first = words[i], last = words[i + want.length - 1];
      return { x: (first.x + last.x + last.w) / 2, y: first.y + first.h / 2 };
    }
  }
  return null;
}

export async function clickAt(p: { x: number; y: number }, after = 400) {
  xdotool("mousemove", String(Math.round(p.x)), String(Math.round(p.y)));
  await sleep(120);
  xdotool("click", "1");
  await sleep(after);
}

/** Clicks the text `phrase` where it is written on screen, waiting up to `timeout` for it to appear. */
export async function clickText(phrase: string, region: () => Box | null = () => null, timeout = 8000) {
  const p = await waitFor(() => find(phrase, region()), timeout, 400);
  if (!p) throw new Error(`"${phrase}" is not on the screen`);
  await clickAt(p);
}

/** Searches until the results show `word` (never part of the query), closing the launcher between tries so indexing,
 * which waits while the user is searching, can go on. Keeps the screen that shows it. */
export async function searchUntilFound(query: string, word: string, timeout = 420_000): Promise<string> {
  let seen = "";
  for (const until = Date.now() + timeout; Date.now() < until;) {
    await search(query);
    seen = resultsText();
    if (seen.toLowerCase().includes(word.toLowerCase())) {
      keepScreen();
      return seen;
    }
    await closeLauncher();
    await sleep(6000);
  }
  keepScreen();
  return seen;
}

/** Searches until the results no longer show `word` (and do show some results). Returns what was shown last. */
export async function searchUntilGone(query: string, word: string, timeout = 420_000): Promise<string> {
  let seen = "";
  for (const until = Date.now() + timeout; Date.now() < until;) {
    await search(query);
    seen = resultsText();
    if (/results/i.test(seen) && !seen.toLowerCase().includes(word.toLowerCase())) {
      keepScreen();
      return seen;
    }
    await closeLauncher();
    await sleep(6000);
  }
  keepScreen();
  return seen;
}

/** Opens the launcher with nothing typed, which shows the index panel while indexing is under way. */
export async function openLauncherEmpty() {
  await openLauncher();
  await tap("ctrl+a");
  await tap("BackSpace");
  await sleep(600);
}

/** Chooses an item of the launcher's main menu (the ≡ button at the top right). */
export async function chooseFromLauncherMenu(item: string) {
  await openLauncherEmpty();
  const box = await openLauncher();
  await clickAt({ x: box.x + box.width - 65, y: box.y + 24 }, 800);
  await clickText(item, () => launcher());
}

/** The "Resume" button of the paused banner (white on blue, which OCR often misses): at the banner's right end. */
export async function pressResume() {
  const box = launcher();
  if (!box) throw new Error("the launcher is closed");
  const banner = find("Indexing paused", box);
  if (!banner) throw new Error("no paused banner");
  await clickAt(find("Resume", box) ?? { x: box.x + box.width - 52, y: banner.y }, 1000);
}

/** Points at the meter of `label` ("CPU" or "Disk") in the index panel and reads its tooltip. */
export async function meterTooltip(label: "CPU" | "Disk"): Promise<string> {
  const box = launcher();
  if (!box) throw new Error("the launcher is closed");
  const panel: Box = { ...box, x: box.x + Math.round(box.width * 0.66), y: box.y + box.height - 220, width: Math.round(box.width * 0.34), height: 150 };
  const at = await waitFor(() => find(label, panel), 5000, 300);
  if (!at) throw new Error(`no ${label} meter on the screen`);
  xdotool("mousemove", String(Math.round(at.x + 120)), String(Math.round(at.y)));
  await sleep(1800);
  const around: Box = { id: "root", x: Math.round(at.x), y: Math.round(at.y), width: 330, height: 60 };
  const text = readRegion(around);
  keepScreen();
  xdotool("mousemove", "5", "5");
  await sleep(300);
  return text;
}

// ---------------------------------------------------------------- the settings window

export const settingsWindow = (): Box | null => windows("^Valafind Settings$")[0] ?? null;

/** Opens Settings with the launcher's settings button (top left). */
export async function openSettings(): Promise<Box> {
  if (!settingsWindow()) {
    const box = await openLauncher();
    await clickAt({ x: box.x + 25, y: box.y + 24 }, 600);
  }
  const win = await waitFor(settingsWindow, 8000, 200);
  if (!win) throw new Error("Settings did not open");
  await sleep(500);
  return win;
}

export async function saveSettings() {
  // "Save" is white on blue, which OCR often misses; it sits right of "Cancel".
  const at = await waitFor(() => find("Save") ?? (({ x, y }) => ({ x: x + 104, y }))(find("Cancel") ?? { x: -1000, y: -1000 }), 8000, 300);
  if (!at || at.x < 0) throw new Error("no Save button in Settings");
  await clickAt(at);
  await waitFor(() => !settingsWindow(), 8000, 200);
  await sleep(800);
}

// The settings window and the folder chooser are found on the whole screen: the window manager's frame makes their
// reported position differ from where they are drawn.

/** Sets a percentage slider ("Maximum CPU", "Maximum disk busy") to `percent` (5 to 100, steps of 5) with the keys. */
export async function setSlider(label: string, percent: number) {
  const at = await waitFor(() => find(label), 8000, 300);
  if (!at) throw new Error(`no "${label}" in Settings`);
  await clickAt({ x: at.x + 150, y: at.y + 33 }, 300);
  await tap("Home");
  for (let v = 5; v < percent; v += 5) await tap("Right", 80);
  await sleep(300);
}

export const folderChooser = (): Box | null => windows("^Choose a folder to index$")[0] ?? null;

/** In Settings, "Add folder…", then picks `folder` (an absolute path) in the folder chooser. */
export async function addFolder(folder: string) {
  await clickText("Add folder");
  const chooser = await waitFor(folderChooser, 8000, 200);
  if (!chooser) throw new Error("the folder chooser did not open");
  await sleep(800);
  // From the Home place, a path typed in the location bar and Enter picks that folder.
  await clickText("Home");
  await tap("ctrl+l");
  await tap("ctrl+a");
  xdotool("type", "--delay", "20", folder.endsWith("/") ? folder : folder + "/");
  await sleep(600);
  await tap("Return", 1500);
  if (folderChooser()) await clickText("Select");
  await waitFor(() => !folderChooser(), 8000, 200);
  await sleep(500);
}

/** In Settings, removes every folder from "Indexed folders", leaving the home folder as the default. */
export async function removeAllFolders() {
  for (let i = 0; i < 10; i++) {
    const p = find("Remove");
    if (!p) return;
    await clickAt(p, 600);
  }
}
