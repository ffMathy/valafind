// ail-derived-from: NarrowResults@3e03f81817a9d529
// End-to-end check for NarrowResults: the kind chips and the "Any time" menu under the list. It sees only what a user
// sees: files saved in the home folder (one dated in the past, as a file copied from an old disk is), clicks on the
// chips and the menu, and the words on the launcher.
import { afterEach, beforeAll, expect } from "bun:test";
import { utimesSync } from "node:fs";
import { closeLauncher, keepScreen, openLauncher, readScreen, search, sleep, tap, test, unique, waitFor, launcher } from "../../desktop.ts";
import { chooseTime, click, deleteSaved, readList, resetFilters, save, searchWhenIndexed, prepareFolders, ask } from "../search-driver.ts";

beforeAll(prepareFolders, 30_000);
afterEach(async () => {
  deleteSaved();
  await resetFilters();
}, 60_000);

test("a kind chip keeps only files of that kind", async () => {
  const id = unique();
  save(`Documents/pears-${id}.txt`, `Pears ${id}: we picked pears in the orchard and made pear jam.`);
  save(`Code/pears-${id}.py`, `# Pears ${id}\ndef count_pears(baskets):\n    return sum(len(b) for b in baskets)\n`);
  await searchWhenIndexed(`pears ${id}`, [`pears-${id}.txt`, `pears-${id}.py`]);
  await click("Code");
  await sleep(1500);
  let list = readList();
  expect(list).toContain(`pears-${id}.py`);
  expect(list).not.toContain(`pears-${id}.txt`);
  keepScreen();
  await click("All");
  await sleep(1500);
  list = readList();
  expect(list).toContain(`pears-${id}.py`);
  expect(list).toContain(`pears-${id}.txt`);
}, 180_000);

test("a kind with no files shows No matching files", async () => {
  const id = unique();
  save(`Documents/plain-${id}.txt`, `Plain ${id}: a note that is not a spreadsheet.`);
  await searchWhenIndexed(`a plain note ${id}`, [`plain-${id}`]);
  await click("Spreadsheets");
  await sleep(1500);
  const seen = readScreen();
  expect(seen).toContain("No matching files");
  expect(seen).toContain("Try describing it differently, pick another filter, or wait for indexing to cover more of your files.");
  expect(seen).toMatch(/0 results . \d+ ?ms/);
  keepScreen();
}, 180_000);

test("the time menu keeps only recent files", async () => {
  const id = unique();
  const old = save(`Documents/old-notes-${id}.txt`, `Old notes ${id}: plums from the garden, and how to keep them.`);
  utimesSync(old, new Date("2020-03-03T10:00:00Z"), new Date("2020-03-03T10:00:00Z"));
  save(`Documents/new-notes-${id}.txt`, `New notes ${id}: plums from the market, and how to cook them.`);
  await searchWhenIndexed(`plums ${id}`, [`old-notes-${id}`, `new-notes-${id}`]);
  expect(readList()).toMatch(new RegExp(`old-notes-${id}\\.txt.{0,40}3 Mar 2020`));
  await chooseTime("Any time", "Past week");
  let seen = readScreen();
  expect(seen).toContain("Past week");
  let list = readList();
  expect(list).toContain(`new-notes-${id}`);
  expect(list).not.toContain(`old-notes-${id}`);
  keepScreen();
  await chooseTime("Past week", "Any time");
  list = readList();
  expect(list).toMatch(new RegExp(`old-notes-${id}\\.txt.{0,40}3 Mar 2020`));
  seen = readScreen();
  expect(seen).toContain("Any time");
}, 180_000);

test("the filters are kept until the user changes them", async () => {
  const id = unique();
  save(`Documents/figs-${id}.txt`, `Figs ${id}: dried figs keep for a year.`);
  save(`Code/figs-${id}.py`, `# Figs ${id}\ndef dry_figs(figs):\n    return [f for f in figs if f.ripe]\n`);
  await searchWhenIndexed(`figs ${id}`, [`figs-${id}.txt`, `figs-${id}.py`]);
  await click("Code");
  await sleep(1200);
  await closeLauncher();
  await tap("ctrl+alt+space", 300);
  expect(await waitFor(launcher, 5000)).not.toBeNull();
  await openLauncher();
  await ask(`dried figs ${id}`);
  await sleep(1500);
  const list = readList();
  expect(list).toContain(`figs-${id}.py`);
  expect(list).not.toContain(`figs-${id}.txt`);
  keepScreen();
}, 180_000);
