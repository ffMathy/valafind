// End-to-end check for SearchFiles: questions typed into the launcher, and the results, excerpts and highlights it
// shows. It sees only what a user sees: files saved in the home folder, keys, and the words and colours on the launcher.
import { afterEach, beforeAll, expect } from "bun:test";
import { keepScreen, readScreen, search, sleep, tap, test, typeText, unique } from "../desktop.ts";
import { deleteSaved, firstResult, highlightedWords, listScores, readList, readPane, resetFilters, save, searchWhenIndexed, prepareFolders, ask } from "./search-driver.ts";

beforeAll(prepareFolders, 30_000);
afterEach(async () => {
  deleteSaved();
  await resetFilters();
}, 60_000);

const lease = (id: string) =>
  `Lejekontrakt ${id} for lejligheden på Nørrebrogade 12, 2200 København N.\n` +
  "Lejer betaler et depositum svarende til tre måneders husleje, i alt 36.000 kr.\n" +
  "Depositummet tilbagebetales ved fraflytning, når lejligheden er synet.\n";

const farm = () =>
  Array.from({ length: 8 }, (_, i) => `Sentence number ${i + 1} talks about the weather in the north of the country and nothing else.`).join(" ") +
  " The pineapple harvest failed in June because of the frost. " +
  Array.from({ length: 4 }, (_, i) => `Line ${i + 1} lists tractor parts for the workshop.`).join(" ");

test("an English question finds a Danish document", async () => {
  const id = unique();
  save(`Documents/Bolig/scan-${id}.txt`, lease(id));
  await searchWhenIndexed("how much was the deposit for the apartment", [`scan-${id}`]);
  const seen = readScreen();
  expect(seen).toContain("Best matches by meaning");
  expect(firstResult()).toBe(`scan-${id}.txt`);
  keepScreen();
}, 180_000);

test("the results come best first, with their count and the time the search took", async () => {
  const id = unique();
  save(`Documents/notes-${id}.txt`, `Notes ${id}: the garden needs water every morning in summer.`);
  save(`Documents/recipe-${id}.txt`, `Recipe ${id}: bake the bread for forty minutes at two hundred degrees.`);
  await searchWhenIndexed("watering the garden", [`notes-${id}`, `recipe-${id}`]);
  const seen = readScreen();
  expect(seen).toContain("Best matches by meaning");
  expect(seen).toMatch(/\d+ results? . \d+ ?ms/);
  const scores = listScores();
  expect(scores.length).toBeGreaterThanOrEqual(2);
  for (let i = 1; i < scores.length; i++) expect(scores[i]!).toBeLessThanOrEqual(scores[i - 1]!);
  keepScreen();
}, 180_000);

test("every indexed file is listed, however weak the match", async () => {
  const id = unique();
  save(`Documents/Bolig/lease-${id}.txt`, lease(id));
  save(`Code/sort-${id}.py`, "def bubble_sort(items):\n    for i in range(len(items)):\n        for j in range(len(items) - i - 1):\n            if items[j] > items[j + 1]:\n                items[j], items[j + 1] = items[j + 1], items[j]\n    return items\n");
  // Only the names' beginnings are compared: OCR may misread a letter of the id.
  await searchWhenIndexed("how much was the deposit for the apartment", ["lease-", "sort-"]);
  const list = readList();
  expect(list.indexOf("lease-")).toBeGreaterThanOrEqual(0);
  expect(list.indexOf("sort-")).toBeGreaterThan(list.indexOf("lease-"));
  keepScreen();
}, 180_000);

test("the sentence closest in meaning is highlighted", async () => {
  const id = unique();
  save(`Documents/Work/budget-${id}.md`,
    "# Quarterly budget meeting\n\nWe agreed to cut the travel budget by twenty percent next quarter.\nMarketing keeps its spend, but the conference booth is cancelled.\n");
  await searchWhenIndexed("the travel budget was cut", [`budget-${id}`]);
  expect(firstResult()).toBe(`budget-${id}.md`);
  // The sentence arrives a moment after the results, from the model's second pass.
  let lit: string[] = [];
  for (const until = Date.now() + 15_000; Date.now() < until; await sleep(1000)) {
    lit = highlightedWords();
    if (lit.includes("agreed")) break;
  }
  expect(readPane()).toContain("Why it matched");
  expect(lit).toContain("agreed");
  expect(lit).toContain("percent");
  expect(lit).not.toContain("marketing");
  expect(lit).not.toContain("meeting");
  keepScreen();
}, 180_000);

test("a weak match highlights the words of the question", async () => {
  const id = unique();
  save(`Documents/farm-${id}.txt`, farm());
  await searchWhenIndexed("pineapple", [`farm-${id}`]);
  expect(firstResult()).toBe(`farm-${id}.txt`);
  await sleep(3000); // time for a second pass, which must not highlight anything else
  const lit = highlightedWords();
  expect(lit).toContain("pineapple");
  expect(lit).not.toContain("harvest");
  expect(lit).not.toContain("weather");
  keepScreen();
}, 180_000);

test("the excerpt is cut around the sentence that matches", async () => {
  const id = unique();
  save(`Documents/farm-${id}.txt`, farm());
  await searchWhenIndexed("pineapple", [`farm-${id}`]);
  expect(firstResult()).toBe(`farm-${id}.txt`);
  const pane = readPane();
  const why = pane.slice(pane.indexOf("Why it matched"), pane.indexOf("Preview"));
  expect(why).toMatch(/Why it matched \S*Sentence number 8 talks/);
  expect(why).not.toContain("Sentence number 7");
  expect(why).toContain("The pineapple harvest failed in June");
  expect(why).toContain("Line 2 lists tractor parts");
  expect(why).not.toContain("Line 3");
  keepScreen();
}, 180_000);

test("a one-letter question still searches", async () => {
  const id = unique();
  save(`Documents/notes-${id}.txt`, `Notes ${id} about the weekly shopping list.`);
  await searchWhenIndexed("a", [`notes-${id}`]);
  const seen = readScreen();
  expect(seen).toMatch(/\d+ results? . \d+ ?ms/);
  expect(listScores().length).toBeGreaterThan(0);
  expect(highlightedWords()).toEqual([]);
  keepScreen();
}, 180_000);

test("a blank question shows the hint again", async () => {
  const id = unique();
  save(`Documents/notes-${id}.txt`, `Notes ${id} about the weekly shopping list.`);
  await searchWhenIndexed("shopping list", [`notes-${id}`]);
  await tap("ctrl+a");
  await tap("BackSpace");
  await sleep(800);
  let seen = readScreen();
  expect(seen).toContain("Search your files by meaning");
  expect(seen).not.toContain(`notes-${id}`);
  await ask("shopping list");
  await sleep(1000);
  expect(readScreen()).toContain(`notes-${id}`);
  await tap("ctrl+a");
  await typeText("   ");
  await sleep(800);
  seen = readScreen();
  expect(seen).toContain("Search your files by meaning");
  expect(seen).not.toContain(`notes-${id}`);
  keepScreen();
}, 180_000);
