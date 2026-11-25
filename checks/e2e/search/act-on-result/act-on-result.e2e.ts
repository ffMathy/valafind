// ail-derived-from: ActOnResult@652fa890a1292cb5
// End-to-end check for ActOnResult: opening a result, showing it in the file manager and copying its path. It sees
// only what a user sees: what was opened (scripts/ail/bin stands in for xdg-open), the clipboard, and the launcher.
import { afterEach, beforeAll, expect } from "bun:test";
import { home, keepScreen, launcher, opened, openLauncher, readScreen, sleep, tap, test, typeText, unique, waitFor } from "../../desktop.ts";
import { click, clickButton, clickResult, clipboard, deleteSaved, readList, readPane, resetFilters, save, searchWhenIndexed, setClipboard, prepareFolders } from "../search-driver.ts";

beforeAll(prepareFolders, 30_000);
afterEach(async () => {
  deleteSaved();
  await resetFilters();
}, 60_000);

/** The chosen result, from the name at the top of the details pane: its part of the bike, or the whole name. */
const chosen = () => /bike-(chain|brakes|tyres)-/.exec(readPane())?.[1] ?? /([\w.-]+\.txt)/.exec(readPane())?.[1] ?? null;
const partOf = (name: string) => /bike-(chain|brakes|tyres)-/.exec(name)?.[1];
const newlyOpened = async (before: number) => (await waitFor(() => opened().length > before && opened().slice(before), 6000)) || [];

/** Saves three notes about bikes, and searches until all three are listed. Returns their names in the list's order. */
async function bikes() {
  const id = unique();
  for (const part of ["chain", "brakes", "tyres"]) save(`Documents/Bike/bike-${part}-${id}.txt`, `Bike ${id}: how to fix the ${part} of a bicycle.`);
  await searchWhenIndexed(`fixing my bicycle ${id}`, [`bike-chain-${id}`, `bike-brakes-${id}`, `bike-tyres-${id}`]);
  await sleep(500);
  // The names in the list's order, spelled as saved (OCR may misread a letter of the id).
  return [...readList().matchAll(/bike-(chain|brakes|tyres)-/g)].map((m) => `bike-${m[1]}-${id}.txt`).slice(0, 3);
}

test("Enter opens the chosen file and closes the launcher", async () => {
  const names = await bikes();
  await tap("Down");
  await sleep(400);
  expect(chosen()).toBe(partOf(names[1]!));
  keepScreen();
  const before = opened().length;
  await tap("Return");
  expect(await newlyOpened(before)).toEqual([`${home()}/Documents/Bike/${names[1]}`]);
  expect(await waitFor(() => !launcher(), 3000)).toBe(true);
}, 180_000);

test("Ctrl+Enter shows the file in the file manager", async () => {
  const names = await bikes();
  keepScreen();
  const before = opened().length;
  await tap("ctrl+Return");
  // No file manager answers on this desktop, so the file's folder is opened.
  expect(await newlyOpened(before)).toEqual([`${home()}/Documents/Bike`]);
  expect(await waitFor(() => !launcher(), 3000)).toBe(true);
  expect(names.length).toBe(3);
}, 180_000);

test("the buttons of the details pane", async () => {
  const names = await bikes();
  let before = opened().length;
  await clickButton("Open");
  expect(await newlyOpened(before)).toEqual([`${home()}/Documents/Bike/${names[0]}`]);
  expect(await waitFor(() => !launcher(), 3000)).toBe(true);
  await openLauncher();
  await sleep(800);
  keepScreen();
  before = opened().length;
  await clickButton("Show in Files");
  expect(await newlyOpened(before)).toEqual([`${home()}/Documents/Bike`]);
  expect(await waitFor(() => !launcher(), 3000)).toBe(true);
}, 180_000);

test("Copy Path copies the full path and the launcher stays open", async () => {
  const names = await bikes();
  setClipboard("nothing yet");
  await clickButton("Copy Path");
  await sleep(500);
  expect(clipboard()).toBe(`${home()}/Documents/Bike/${names[0]}`);
  expect(launcher()).not.toBeNull();
  keepScreen();
}, 180_000);

test("Ctrl+C copies the chosen file's path unless text is selected in the search box", async () => {
  const id = unique();
  save(`Documents/deposit-${id}.txt`, `Deposit ${id}: the deposit for the flat is three months of rent.`);
  await searchWhenIndexed("deposit", [`deposit-${id}`]);
  expect(chosen()).toMatch(/^deposit-/);
  await tap("End"); // nothing selected in the search box
  setClipboard("nothing yet");
  await tap("ctrl+c");
  await sleep(500);
  expect(clipboard()).toBe(`${home()}/Documents/deposit-${id}.txt`);
  await tap("ctrl+a");
  await tap("ctrl+c");
  await sleep(500);
  expect(clipboard()).toBe("deposit");
  keepScreen();
}, 180_000);

test("a click chooses a result, a double click opens it", async () => {
  const names = await bikes();
  await clickResult(`bike-${partOf(names[2]!)}`);
  await sleep(500);
  expect(chosen()).toBe(partOf(names[2]!));
  keepScreen();
  const before = opened().length;
  await clickResult(`bike-${partOf(names[2]!)}`, true);
  expect(await newlyOpened(before)).toEqual([`${home()}/Documents/Bike/${names[2]}`]);
  expect(await waitFor(() => !launcher(), 3000)).toBe(true);
}, 180_000);

test("Enter with no results does nothing", async () => {
  await openLauncher();
  await tap("ctrl+a");
  await typeText(`nothing here ${unique()}`);
  await sleep(1500);
  await click("Spreadsheets");
  await sleep(1500);
  expect(readScreen()).toContain("No matching files");
  const before = opened().length;
  await tap("Return");
  await sleep(1500);
  expect(opened().length).toBe(before);
  expect(launcher()).not.toBeNull();
  keepScreen();
}, 60_000);
