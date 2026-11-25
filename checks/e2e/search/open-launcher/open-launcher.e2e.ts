// End-to-end check for OpenLauncher: Ctrl+Alt+Space, Esc and a click elsewhere, on a real X11 desktop. It sees only
// what a user sees: whether a window titled "Valafind" is on screen, where it is, and the words on it.
import { afterAll, afterEach, beforeAll, expect } from "bun:test";
import { closeLauncher, keepScreen, launcher, openLauncher, readScreen, search, sleep, tap, test, typeText, unique, waitFor, windows, xdotool } from "../../desktop.ts";
import { deleteSaved, readPane, save, searchWhenIndexed, prepareFolders } from "../search-driver.ts";

beforeAll(prepareFolders, 30_000);
let other: ReturnType<typeof Bun.spawn> | null = null;
afterEach(() => deleteSaved());
afterAll(() => other?.kill());

const pressShortcut = () => tap("ctrl+alt+space", 300);

test("Ctrl+Alt+Space opens the launcher, ready to type into", async () => {
  await closeLauncher();
  expect(launcher()).toBeNull();
  await pressShortcut();
  expect(await waitFor(launcher, 5000)).not.toBeNull();
  await sleep(500);
  const word = `kiwi${unique()}`;
  await tap("ctrl+a");
  await typeText(word);
  await sleep(600);
  expect(readScreen()).toContain(word);
  keepScreen();
}, 30_000);

test("Ctrl+Alt+Space closes it again", async () => {
  await closeLauncher();
  await pressShortcut();
  expect(await waitFor(launcher, 5000)).not.toBeNull();
  await sleep(500);
  await pressShortcut();
  expect(await waitFor(() => !launcher(), 3000)).toBe(true);
  keepScreen();
}, 30_000);

test("Esc closes it", async () => {
  await openLauncher();
  await sleep(300);
  await tap("Escape");
  expect(await waitFor(() => !launcher(), 3000)).toBe(true);
  keepScreen();
}, 30_000);

test("the question and its results are kept for the next time", async () => {
  const id = unique();
  save(`Documents/pears-${id}.txt`, `Pears ${id}: the pear tree in the garden gave forty kilos this autumn.`);
  save(`Documents/apples-${id}.txt`, `Apples ${id}: the apple tree in the garden gave twenty kilos this autumn.`);
  await searchWhenIndexed(`fruit trees ${id}`, [`pears-${id}`, `apples-${id}`]);
  await tap("Down");
  await sleep(500);
  const chosen = /([\w.-]+\.txt)/.exec(readPane())?.[1];
  expect(chosen).toBeTruthy();
  await tap("Escape");
  expect(await waitFor(() => !launcher(), 3000)).toBe(true);
  await pressShortcut();
  expect(await waitFor(launcher, 5000)).not.toBeNull();
  await sleep(800);
  const seen = readScreen();
  expect(seen).toContain(`fruit trees ${id}`);
  expect(/([\w.-]+\.txt)/.exec(readPane())?.[1]).toBe(chosen);
  keepScreen();
  await typeText("plums");
  await sleep(600);
  const after = readScreen();
  expect(after).toContain("plums");
  expect(after).not.toContain(`fruit trees ${id}`);
}, 180_000);

test("opened with the shortcut, it closes when the user clicks another window", async () => {
  const title = `Other window ${unique()}`;
  other = Bun.spawn(["xterm", "-geometry", "60x10+0+0", "-T", title], { env: process.env, stdout: "ignore", stderr: "ignore" });
  const box = await waitFor(() => windows(`^${title}$`)[0], 5000);
  expect(box).toBeTruthy();
  await closeLauncher();
  await pressShortcut();
  expect(await waitFor(launcher, 5000)).not.toBeNull();
  await sleep(800); // past the moment after opening in which a click elsewhere is ignored
  // The launcher covers most of the screen: click the part of the other window it leaves free, at the left.
  xdotool("mousemove", String(box!.x + 10), String(box!.y + 60), "click", "1");
  expect(await waitFor(() => !launcher(), 3000)).toBe(true);
  keepScreen();
  other.kill();
  other = null;
}, 30_000);

test("it appears centred across the screen, near the top", async () => {
  await closeLauncher();
  await pressShortcut();
  const box = await waitFor(launcher, 5000);
  expect(box).not.toBeNull();
  const screen = /dimensions:\s+(\d+)x(\d+)/.exec(Bun.spawnSync(["xdpyinfo"], { env: process.env }).stdout.toString());
  const [width, height] = [Number(screen?.[1]), Number(screen?.[2])];
  expect(Math.abs(box!.x - (width - box!.width) / 2)).toBeLessThanOrEqual(2);
  expect(box!.y).toBeLessThanOrEqual(Math.round(height * 0.22) + 2);
  expect(box!.y).toBeGreaterThanOrEqual(0);
  keepScreen();
}, 30_000);
