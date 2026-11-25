// What the search checks need beyond checks/e2e/desktop.ts, still only what a user sees and does: where a word is on
// the screen (to click a chip, a menu item or a button), which words of the launcher are highlighted, the first result,
// the clipboard, and searching while the index catches up with files just saved.
//
// Needs, besides desktop.ts: ImageMagick (import, convert), tesseract and xclip.
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { deleteFile, home, keepScreen, launcher, openLauncher, readScreen, saveFile, search, sleep, tap, type Box, xdotool } from "../desktop.ts";

function run(cmd: string[], input?: string): string {
  // One thread for tesseract: on a busy machine its thread pool makes it many times slower.
  const r = Bun.spawnSync(cmd, {
    env: { ...process.env, OMP_THREAD_LIMIT: "1" },
    stdin: input === undefined ? "ignore" : Buffer.from(input),
    stdout: "pipe",
    stderr: "pipe",
  });
  return r.stdout.toString();
}

export type Word = { text: string; x: number; y: number; width: number; height: number; highlighted: boolean };

// The highlight behind a matching word: a light blue, darker than the blue of the selected row. Measured on the
// launcher: (214, 230, 250) on white and (199, 221, 249) on the selected row, while the selected row is (236, 244, 253).
const isHighlight = (r: number, g: number, b: number) => r >= 185 && r <= 222 && g >= 210 && g <= 238 && b >= 240 && b - r >= 25;

/**
 * The words on a window (the launcher by default) or the whole screen, read with OCR, each with where it is on the
 * screen and whether it sits on the highlight colour.
 */
export function words(box: Box | null = launcher()): Word[] {
  const dir = mkdtempSync(join(tmpdir(), "ail-search-"));
  try {
    const shot = join(dir, "shot.png"), big = join(dir, "big.png"), raw = join(dir, "shot.rgb");
    run(["import", "-window", box ? box.id : "root", shot]);
    const size = run(["identify", "-format", "%w %h", shot]).trim().split(" ").map(Number);
    const [w, h] = [size[0] ?? 0, size[1] ?? 0];
    run(["convert", shot, "-depth", "8", `rgb:${raw}`]);
    const rgb = readFileSync(raw);
    run(["convert", shot, "-resize", "200%", "-colorspace", "Gray", big]);
    const tsv = run(["tesseract", big, "stdout", "--psm", "11", "tsv"]);
    const origin = box ? { x: box.x, y: box.y } : { x: 0, y: 0 };
    const out: Word[] = [];
    for (const line of tsv.split("\n").slice(1)) {
      const f = line.split("\t");
      const text = (f[11] ?? "").trim();
      if (f.length < 12 || !text || Number(f[10]) < 0) continue;
      const [left, top, width, height] = [f[6], f[7], f[8], f[9]].map((v) => Math.round(Number(v) / 2));
      let lit = 0, all = 0;
      for (let y = top!; y < Math.min(h, top! + height!); y++)
        for (let x = left!; x < Math.min(w, left! + width!); x++) {
          const i = (y * w + x) * 3;
          all++;
          if (isHighlight(rgb[i]!, rgb[i + 1]!, rgb[i + 2]!)) lit++;
        }
      out.push({ text, x: origin.x + left!, y: origin.y + top!, width: width!, height: height!, highlighted: all > 0 && lit / all > 0.25 });
    }
    return out;
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

const plain = (s: string) => s.toLowerCase().replace(/[^\p{L}\p{N}]+/gu, "");

/** The highlighted words of the launcher, lower-cased, without punctuation. */
export const highlightedWords = () => words().filter((w) => w.highlighted).map((w) => plain(w.text)).filter(Boolean);

/**
 * Where a phrase is on the screen: the words of `phrase` next to each other on one line. `prefer` picks among several
 * places: the lowest (chips, the footer, buttons) or the highest.
 */
export function find(phrase: string, prefer: "lowest" | "highest" = "lowest", box: Box | null = launcher(), above = Infinity) {
  // OCR may glue words ("Anytime"), so compare the letters of runs of words, without the spaces.
  const want = plain(phrase);
  const ws = words(box).filter((w) => w.y < above);
  const hits: { x: number; y: number }[] = [];
  for (let i = 0; i < ws.length; i++) {
    let text = "";
    for (let j = i; j < ws.length && text.length < want.length && Math.abs(ws[j]!.y - ws[i]!.y) < 8; j++) {
      text += plain(ws[j]!.text);
      if (text === want) {
        const [first, last] = [ws[i]!, ws[j]!];
        hits.push({ x: Math.round((first.x + last.x + last.width) / 2), y: Math.round(first.y + first.height / 2) });
      }
    }
  }
  hits.sort((a, b) => (prefer === "lowest" ? b.y - a.y : a.y - b.y));
  return hits[0] ?? null;
}

/** Clicks a phrase shown on the launcher (or, with `box` null, anywhere on the screen, such as a menu). */
export async function click(phrase: string, prefer: "lowest" | "highest" = "lowest", box: Box | null = launcher(), double = false, above = Infinity) {
  const at = find(phrase, prefer, box, above);
  if (!at) throw new Error(`"${phrase}" is not on the screen`);
  xdotool("mousemove", String(at.x), String(at.y), "click", ...(double ? ["--repeat", "2", "--delay", "80"] : []), "1");
  await sleep(600);
}

/** Clicks a button of the details pane ("Open", "Show in Files", "Copy Path"), above the footer's key hints. */
export const clickButton = (label: string) => click(label, "lowest", launcher(), false, (launcher()?.y ?? 0) + 730);

/** Picks an entry of the "Any time" menu. */
export async function chooseTime(current: string, choice: string) {
  await click(current);
  await sleep(400);
  await click(choice, "highest", null);
  await sleep(1200);
}

/** The file name shown first in the list of results. */
export const firstResult = (list = readList()) => /([\w.-]+\.[a-z]{2,4})\b/.exec(list)?.[1] ?? null;

/**
 * The folders the checks save into, made once and given a few seconds before any file goes in: a file saved into a
 * folder made a moment before can be missed until the next full check of the folders.
 */
const FOLDERS = ["Documents", "Documents/Work", "Documents/Bolig", "Documents/Bike", "Code"];
export async function prepareFolders() {
  if (FOLDERS.every((f) => existsSync(join(home(), f)))) return;
  for (const f of FOLDERS) mkdirSync(join(home(), f), { recursive: true });
  await sleep(5000);
}

/** Files a check saved, so it can delete them when it is done and leave the index as it found it. */
const saved: string[] = [];
export function save(relativePath: string, content: string | Uint8Array): string {
  saved.push(relativePath);
  return saveFile(relativePath, content);
}
export function deleteSaved() {
  for (const f of saved.splice(0)) deleteFile(f);
}

/**
 * Searches until the launcher shows every one of `words` (case ignored). Between tries the question is cleared for a
 * few seconds: indexing waits while a question is on screen, so a file saved just now is indexed in those pauses.
 */
export async function searchWhenIndexed(query: string, words: string[], timeout = 150_000): Promise<string> {
  let seen = "";
  for (const until = Date.now() + timeout; Date.now() < until; ) {
    await search(query);
    await sleep(1500);
    seen = readScreen();
    if (words.every((w) => seen.toLowerCase().includes(w.toLowerCase()))) break;
    await tap("ctrl+a");
    await tap("BackSpace");
    await sleep(4000);
  }
  keepScreen();
  return seen;
}

/** Puts the filters back to "All" and "Any time", so each check starts from the launcher as it first opens. */
export async function resetFilters() {
  await openLauncher();
  const chips = words().filter((w) => w.y > (launcher()?.y ?? 0) + 680).map((w) => plain(w.text)).join("");
  if (!chips.includes("anytime")) {
    const shown = ["Past week", "Past month", "Past year", "Today"].find((t) => chips.includes(plain(t)));
    if (shown) await chooseTime(shown, "Any time");
  }
  // The chosen chip is white on blue, which OCR does not read: "All" is readable only when another chip is chosen.
  if (find("All")) await click("All");
}

/** What the clipboard holds as text. */
export const clipboard = () => run(["xclip", "-o", "-selection", "clipboard"]);

/** Puts text on the clipboard, as another app would. */
export const setClipboard = (text: string) => {
  Bun.spawnSync(["xclip", "-selection", "clipboard"], { stdin: Buffer.from(text), env: process.env });
};

/** The words in a part of the launcher, given relative to its top-left corner, read with OCR. */
export function readPart(x: number, y: number, width: number, height: number, box: Box | null = launcher()): string {
  if (!box) return "";
  const dir = mkdtempSync(join(tmpdir(), "ail-search-"));
  try {
    const shot = join(dir, "shot.png"), big = join(dir, "big.png");
    run(["import", "-window", box.id, shot]);
    run(["convert", shot, "-crop", `${width}x${height}+${x}+${y}`, "+repage", "-resize", "200%", "-colorspace", "Gray", big]);
    return run(["tesseract", big, "stdout", "--psm", "6"]).replace(/[“”]/g, '"').replace(/\s+/g, " ").trim();
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
}

/** The list of results (left of the details pane, or the whole width when the pane is hidden). */
export const readList = (wide = false) => readPart(18, 88, wide ? 1184 : 735, 600);

/** The details pane, right of the list. */
export const readPane = () => readPart(762, 88, 440, 600);

/** The match scores in the list, top to bottom. */
export const listScores = (list = readList()) => [...list.matchAll(/\b([01][.,]\d\d)\b/g)].map((m) => Number(m[1]!.replace(",", ".")));
