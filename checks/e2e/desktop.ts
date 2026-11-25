// Drives an X11 desktop the way a person would, for the end-to-end checks of Valafind. It knows nothing of
// Valafind's code: it puts files into the home folder Valafind indexes (as a user saves them), presses real keys
// (xdotool, through XTEST), and reads what the launcher shows from the screen (tesseract OCR). So it works against
// any build of the app.
//
// Needs: DISPLAY, xdotool, ImageMagick (import, convert), tesseract, and VALAFIND_HOME, the home folder Valafind runs
// with (relative to the project root), whose files it indexes.
import { test as bunTest } from "bun:test";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";

const root = resolve(import.meta.dir, "../..");
export const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

/** A short id of letters OCR reads reliably, so texts from different tests never collide. */
export const unique = () => Array.from({ length: 6 }, () => "acdefhkmnprtwxyz"[Math.floor(Math.random() * 16)]).join("");

// ---------------------------------------------------------------- the user's files

/** The home folder Valafind indexes. */
export const home = () => resolve(root, process.env.VALAFIND_HOME ?? ".ail/legacy/home");

/** Saves a file under the home folder, as a user would, and returns its full path. */
export function saveFile(relativePath: string, content: string | Uint8Array): string {
  const path = join(home(), relativePath);
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, content);
  return path;
}

/** Deletes a file or folder under the home folder. */
export const deleteFile = (relativePath: string) => rmSync(join(home(), relativePath), { recursive: true, force: true });

// ---------------------------------------------------------------- keys and windows

function run(cmd: string[], input?: string): string {
  const r = Bun.spawnSync(cmd, { env: process.env, stdin: input === undefined ? "ignore" : Buffer.from(input), stdout: "pipe", stderr: "pipe" });
  return r.stdout.toString();
}
export const xdotool = (...args: string[]) => run(["xdotool", ...args]);

/** xdotool key names: ctrl, alt, shift, Up, Down, Return, Escape, Tab, space, a … */
export const keyDown = async (key: string, after = 60) => { xdotool("keydown", key); await sleep(after); };
export const keyUp = async (key: string, after = 60) => { xdotool("keyup", key); await sleep(after); };
export const tap = async (keys: string, after = 120) => { xdotool("key", "--clearmodifiers", keys); await sleep(after); };
export const typeText = async (text: string) => { xdotool("type", "--delay", "25", text); await sleep(150); };
export const releaseAll = async () => { for (const k of ["ctrl", "alt", "shift", "super"]) xdotool("keyup", k); await sleep(80); };

export type Box = { id: string; x: number; y: number; width: number; height: number };

/** The visible windows whose title matches `title` (a regular expression), largest first. */
export function windows(title: string): Box[] {
  return xdotool("search", "--onlyvisible", "--name", title).split("\n").filter(Boolean).map((id) => {
    const g = xdotool("getwindowgeometry", id);
    const p = /Position:\s*(-?\d+),(-?\d+)/.exec(g), s = /Geometry:\s*(\d+)x(\d+)/.exec(g);
    return { id, x: Number(p?.[1] ?? 0), y: Number(p?.[2] ?? 0), width: Number(s?.[1] ?? 0), height: Number(s?.[2] ?? 0) };
  }).sort((a, b) => b.width * b.height - a.width * a.height);
}

/** The launcher: the visible window titled "Valafind"; null when it is closed. */
export const launcher = (): Box | null => windows("^Valafind$")[0] ?? null;

export async function waitFor<T>(probe: () => T | null | undefined | false, timeout = 5000, every = 100): Promise<T | null> {
  for (const until = Date.now() + timeout; ; await sleep(every)) {
    const v = probe();
    if (v) return v;
    if (Date.now() > until) return null;
  }
}

/** Opens the launcher with the global shortcut (Ctrl+Alt+Space) unless it is open, and returns it. */
export async function openLauncher(): Promise<Box> {
  let box = launcher();
  if (!box) {
    await tap("ctrl+alt+space");
    box = await waitFor(launcher, 5000);
  }
  if (!box) throw new Error("the launcher did not open with Ctrl+Alt+Space");
  return box;
}

/** Closes the launcher with Esc if it is open. */
export async function closeLauncher() {
  if (!launcher()) return;
  await tap("Escape");
  await waitFor(() => !launcher(), 3000);
}

/** Opens the launcher and searches for `query`, replacing what was typed before. */
export async function search(query: string) {
  await openLauncher();
  await tap("ctrl+a");
  await typeText(query);
  await sleep(1200);
}

// ---------------------------------------------------------------- reading the screen

/** The words in a window (or the whole screen), read with OCR. White space is made single. */
export function readScreen(box: Box | null = launcher(), scale = 2): string {
  const dir = mkdtempSync(join(tmpdir(), "ail-ocr-"));
  const shot = join(dir, "shot.png"), big = join(dir, "big.png");
  run(["import", "-window", box ? box.id : "root", shot]);
  run(["convert", shot, "-resize", `${scale * 100}%`, "-colorspace", "Gray", big]);
  const text = run(["tesseract", big, "stdout", "--psm", "6"]);
  rmSync(dir, { recursive: true, force: true });
  return text.replace(/[“”]/g, '"').replace(/[‘’]/g, "'").replace(/\s+/g, " ").trim();
}

/**
 * Searches for `query` until the launcher shows every one of `words` (case ignored), as the index catches up with new
 * files. Returns what the launcher showed last.
 */
export async function searchUntilShown(query: string, words: string[], timeout = 120_000): Promise<string> {
  let seen = "";
  for (const until = Date.now() + timeout; Date.now() < until; await sleep(2000)) {
    await search(query);
    seen = readScreen();
    if (words.every((w) => seen.toLowerCase().includes(w.toLowerCase()))) break;
  }
  keepScreen();
  return seen;
}

/** What Valafind opened (a file, a folder or a link), as recorded by the stand-in for xdg-open, in order. */
export function opened(): string[] {
  const file = join(root, ".ail", "opened", `${(process.env.DISPLAY ?? "none").replace(/[^a-zA-Z0-9]/g, "")}.txt`);
  return Bun.file(file).size ? run(["cat", file]).split("\n").filter(Boolean) : [];
}

// ---------------------------------------------------------------- screenshots as proof

/** The file name `ail adopt` expects for a test's screenshot (the same rule as AIL's own slugOf). */
export const shotName = (test: string) =>
  `${test.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "").slice(0, 60) || "test"}.png`;

/** The whole screen of an X display, as PNG bytes; empty when it cannot be taken. */
export function screenOf(display = process.env.DISPLAY ?? ""): Uint8Array {
  const r = Bun.spawnSync(["import", "-window", "root", "png:-"], { env: { ...process.env, DISPLAY: display }, stdout: "pipe", stderr: "ignore" });
  return r.exitCode === 0 ? r.stdout : new Uint8Array();
}

let keptScreen: Uint8Array | null = null;
let pendingScreen: Promise<void> | null = null;

/**
 * Keeps the screen as it is now as the scenario's proof, in place of the screen it ends on. Call it at the moment the
 * check sees what the scenario is about (the results of a search, a notification); the last one kept wins.
 */
export function keepScreen(display = process.env.DISPLAY ?? "") {
  const png = screenOf(display);
  if (png.length) keptScreen = png;
}

/** Like keepScreen, but without waiting for the screenshot, for helpers that time what is on screen. */
export function keepScreenSoon(display = process.env.DISPLAY ?? "") {
  const p = Bun.spawn(["import", "-window", "root", "png:-"], { env: { ...process.env, DISPLAY: display }, stdout: "pipe", stderr: "ignore" });
  pendingScreen = (async () => {
    const png = new Uint8Array(await new Response(p.stdout).arrayBuffer());
    if ((await p.exited) === 0 && png.length) keptScreen = png;
  })();
}

/** For a scenario played on a desktop of its own: keeps that desktop's screen on closing it, unless one was kept. */
export function keepScreenIfNone(display: string) {
  if (!keptScreen) keepScreen(display);
}

/**
 * A bun:test test that, once its scenario has run (passed or not), saves the kept screen, or else the screen it ended
 * on, as `AIL_SHOTS_DIR/<test name as a slug>.png`, the proof `ail adopt` collects. Use it in place of bun:test's `test`.
 */
export function test(name: string, body: () => Promise<unknown> | unknown, timeout?: number) {
  bunTest(name, async () => {
    keptScreen = null;
    pendingScreen = null;
    try {
      await body();
    } finally {
      await pendingScreen;
      const dir = process.env.AIL_SHOTS_DIR;
      if (dir) {
        const png = keptScreen ?? screenOf();
        if (png.length) writeFileSync(join(dir, shotName(name)), png);
      }
      keptScreen = null;
    }
  }, timeout);
}
