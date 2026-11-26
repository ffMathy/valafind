// Helpers for the end-to-end checks of what Valafind indexes, by kind of file. They make small files the way other
// apps save them (python3 for office documents and e-mail, ImageMagick for pictures, ffmpeg for sound and video) into
// the home folder Valafind indexes, and compare where files land in what the launcher shows. Nothing here touches
// Valafind's code, logs or index.
import { readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { closeLauncher, deleteFile, home, keepScreen, readScreen, saveFile, search, sleep, unique } from "../desktop.ts";

// Under a busy CPU, tesseract's own threads make each reading of the screen many times slower.
process.env.OMP_THREAD_LIMIT ??= "1";

const root = resolve(import.meta.dir, "../../..");
const fixtures = join(root, "tests/Valafind.Core.Tests/Fixtures/Media");

function run(cmd: string[]) {
  const r = Bun.spawnSync(cmd, { stdout: "pipe", stderr: "pipe" });
  if (r.exitCode !== 0) throw new Error(`${cmd[0]} failed: ${r.stderr.toString()}`);
}

/** A folder of its own for one check, under the home folder; removed again by `cleanUp`. */
const folders: string[] = [];
export function folder(): string {
  const name = `kinds-${unique()}`;
  folders.push(name);
  return name;
}
export const cleanUp = () => { for (const f of folders.splice(0)) deleteFile(f); };

/** Saves a document of one format (docx, xlsx, pptx, odt, epub, rtf, pdf, eml, mbox, ipynb); see make-file.py. */
export function saveDocument(relativePath: string, format: string, paragraphs: string[]): string {
  const path = saveFile(relativePath, "");
  run(["python3", "-I", join(import.meta.dir, "make-file.py"), format, path, JSON.stringify(paragraphs)]);
  return path;
}

/** Saves a picture drawn with ImageMagick: a red circle, a blue square, or a word in large black letters. */
export function savePicture(relativePath: string, what: { circle: "red" } | { square: "blue" } | { word: string }): string {
  const path = saveFile(relativePath, "");
  const draw = "circle" in what ? ["-size", "320x240", "xc:white", "-fill", "red", "-draw", "circle 160,120 160,40"]
    : "square" in what ? ["-size", "320x240", "xc:white", "-fill", "blue", "-draw", "rectangle 90,50 230,190"]
    : ["-size", "640x200", "xc:white", "-font", "DejaVu-Sans-Bold", "-pointsize", "90", "-fill", "black", "-gravity", "center", "-annotate", "+0+0", what.word];
  run(["convert", ...draw, path]);
  return path;
}

/** Copies one of the test fixtures (speech.wav and speech.mp3 read out a weather forecast) into the home folder. */
export function saveFixture(name: string, relativePath: string): string {
  return saveFile(relativePath, readFileSync(join(fixtures, name)));
}

/** Runs ffmpeg with `args` and writes its output to `relativePath` in the home folder. Inputs named "fixture:x" are fixtures. */
export function saveWithFfmpeg(relativePath: string, args: string[]): string {
  const path = saveFile(relativePath, "");
  const resolved = args.map((a) => a.startsWith("fixture:") ? join(fixtures, a.slice(8)) : a.startsWith("home:") ? join(home(), a.slice(5)) : a);
  run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", ...resolved, path]);
  return path;
}

/** Text that is a few thousand characters of filler, so that what follows lies beyond the part Valafind reads. */
export const filler = (chars: number) =>
  "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. "
    .repeat(Math.ceil(chars / 120)).slice(0, chars);

/**
 * Where `name` is in what the launcher shows: the whole name, or else the part after its first "-" (when that has 6
 * characters or more), since OCR now and then misreads a letter of the random code in front.
 */
function locate(seen: string, name: string): number {
  // OCR now and then reads "photo.tiff" as "photo. tiff": spaces around a dot are not part of a file name.
  const dots = (t: string) => t.toLowerCase().replace(/\s*\.\s*/g, ".");
  const s = dots(seen), n = dots(name), whole = s.indexOf(n);
  const rest = n.slice(n.indexOf("-") + 1);
  return whole >= 0 || !n.includes("-") || rest.length < 6 ? whole : s.indexOf(rest);
}

/** True when the launcher shows the file `name` (see locate for what OCR may get wrong). */
export const shows = (seen: string, name: string) => locate(seen, name) >= 0;

/** True when `first` is shown and comes before `then` in the launcher (or `then` is not shown at all). */
export function shownAbove(seen: string, first: string, then: string): boolean {
  const a = locate(seen, first), b = locate(seen, then);
  return a >= 0 && (b < 0 || a < b);
}

/**
 * Searches for `query` until `done` holds for what the launcher shows, as the index catches up with new files. Between
 * tries the launcher is closed for a while: Valafind holds back indexing while a search is on screen, and reading the
 * screen takes the processor that indexing paces itself by. Returns what the launcher showed last, and keeps that
 * screen as the scenario's proof.
 */
export async function searchUntil(query: string, done: (seen: string) => boolean, timeout = 300_000): Promise<string> {
  let seen = "";
  for (const until = Date.now() + timeout; ;) {
    await search(query);
    seen = readScreen();
    if (done(seen) || Date.now() > until) break;
    await closeLauncher();
    await sleep(8000);
  }
  keepScreen();
  return seen;
}

/** Searches for `query` until the launcher shows every one of `words` (case ignored). */
export const searchUntilFound = (query: string, words: string[], timeout?: number) =>
  searchUntil(query, (seen) => words.every((w) => seen.toLowerCase().includes(w.toLowerCase()) || shows(seen, w)), timeout);

/** Searches for `query` until the launcher shows `first` above each of `below`. */
export const searchUntilAbove = (query: string, first: string, below: string[], timeout?: number) =>
  searchUntil(query, (seen) => below.every((b) => shownAbove(seen, first, b)), timeout);
