// ail-derived-from: LauncherKeys@cfdd6da4d7f5ac68
// End-to-end check for LauncherKeys: the arrows, Tab and the footer of the launcher, on a real X11 desktop. It sees
// only what a user sees: the words on the launcher, read with OCR.
import { afterEach, beforeAll, expect } from "bun:test";
import { keepScreen, readScreen, sleep, tap, test, unique } from "../../desktop.ts";
import { deleteSaved, firstResult, readList, readPane, readPart, save, searchWhenIndexed, prepareFolders, readQuery } from "../search-driver.ts";

beforeAll(prepareFolders, 30_000);
afterEach(() => deleteSaved());

/** The kind of tea in the name shown at the top of the details pane: the chosen result. */
const chosen = () => /tea-(\w+?)-/.exec(readPane())?.[1] ?? null;
const teaOf = (name: string | null | undefined) => (name ? /tea-(\w+?)-/.exec(name)?.[1] ?? null : null);

/** Saves three notes about tea, and searches until all three are listed. Returns the names in the list's order. */
async function threeResults(): Promise<string[]> {
  const id = unique();
  for (const kind of ["green", "black", "mint"]) save(`Documents/tea-${kind}-${id}.txt`, `Tea ${id}: how to brew ${kind} tea, and for how long.`);
  await searchWhenIndexed(`brewing tea ${id}`, [`tea-green-${id}`, `tea-black-${id}`, `tea-mint-${id}`]);
  await sleep(500);
  return [...readList().matchAll(/(tea-\w+-\w+\.txt)/g)].map((m) => m[1]!).slice(0, 3);
}

test("the first result is chosen as the results come", async () => {
  const names = await threeResults();
  expect(chosen()).toBe(teaOf(firstResult()));
  expect(chosen()).toBe(teaOf(names[0]));
  keepScreen();
}, 180_000);

test("Down and Up move through the results, stopping at both ends", async () => {
  const names = await threeResults();
  expect(names.length).toBe(3);
  // These three are the best matches, so they are the first three rows.
  await tap("Down");
  await sleep(400);
  expect(chosen()).toBe(teaOf(names[1]));
  await tap("Down");
  await tap("Down");
  await sleep(400);
  expect(chosen()).toBe(teaOf(names[2]));
  keepScreen();
  await tap("Up");
  await tap("Up");
  await tap("Up");
  await sleep(400);
  expect(chosen()).toBe(teaOf(names[0]));
}, 180_000);

test("Tab hides the details pane and shows it again", async () => {
  await threeResults();
  expect(readPane()).toContain("Copy Path");
  await tap("Tab");
  await sleep(600);
  expect(readScreen()).not.toContain("Copy Path");
  keepScreen();
  await tap("Tab");
  await sleep(600);
  expect(readScreen()).toContain("Copy Path");
}, 180_000);

test("the footer shows the keys", async () => {
  await threeResults();
  const footer = readPart(0, 746, 1220, 40);
  expect(footer).toMatch(/navigate/);
  expect(footer).toMatch(/open/);
  expect(footer).toMatch(/Ctrl.{0,4}show ?in Files/);
  expect(footer).toMatch(/Tab preview/);
  keepScreen();
}, 180_000);

test("the arrows and Tab never reach the search box", async () => {
  const id = unique();
  save(`Documents/travel-${id}.txt`, `Travel ${id}: we cut the travel budget for the spring.`);
  await searchWhenIndexed("travel budget", [`travel-${id}`]);
  await tap("Up");
  await tap("Down");
  await tap("Tab");
  await sleep(600);
  // Still the question as typed: nothing was added to it, and the caret is where it was, at the end.
  await tap("s");
  await sleep(600);
  expect(readQuery()).toBe("travel budgets");
  keepScreen();
  await tap("Tab");
}, 180_000);
