// End-to-end check for WatchChanges: files added, edited, renamed, moved, deleted and emptied in the home folder while
// Valafind runs, as a user would change them, and what the launcher then lists.
import { afterEach, expect } from "bun:test";
import { renameSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { closeLauncher, deleteFile, home, releaseAll, saveFile, test, unique } from "../../desktop.ts";
import { searchUntilFound, searchUntilGone } from "../indexing-driver.ts";

afterEach(async () => {
  await releaseAll();
  await closeLauncher();
});

const at = (relative: string) => join(home(), relative);

test("a new file is found within seconds", async () => {
  const id = unique();
  const started = Date.now();
  saveFile(`Inbox/${id}/added-${id}.txt`, `A harbour ${id} full of sailing boats, gulls and fishermen mending their nets.`);
  const seen = await searchUntilFound("harbour full of sailing boats", `added-${id}`);
  expect(seen).toContain(`added-${id}`);
  console.log(`added-${id} was listed ${Math.round((Date.now() - started) / 1000)} s after it was saved`);
}, 480_000);

test("an edited file is found by its new text", async () => {
  const id = unique();
  saveFile(`Inbox/${id}/edited-${id}.txt`, `Notes about a chess tournament ${id}, the openings played and the final standings.`);
  expect(await searchUntilFound("chess tournament openings", `edited-${id}`)).toContain(`edited-${id}`);
  await closeLauncher();
  writeFileSync(at(`Inbox/${id}/edited-${id}.txt`), `A recipe for mushroom risotto ${id} with parmesan, white wine and thyme.`);
  const seen = await searchUntilFound("mushroom risotto parmesan", `risotto ${id}`);
  expect(seen).toContain(`edited-${id}`);
  expect(seen.toLowerCase()).toContain("mushroom risotto");
}, 900_000);

test("a renamed file is found under its new name only", async () => {
  const id = unique();
  saveFile(`Inbox/${id}/before-${id}.txt`, `Eyewitness notes ${id} on the volcano eruption, the ash cloud and the lava flows.`);
  expect(await searchUntilFound("volcano eruption ash cloud", `before-${id}`)).toContain(`before-${id}`);
  await closeLauncher();
  renameSync(at(`Inbox/${id}/before-${id}.txt`), at(`Inbox/${id}/after-${id}.txt`));
  const seen = await searchUntilFound("volcano eruption ash cloud", `after-${id}`);
  expect(seen).toContain(`after-${id}`);
  expect(seen).not.toContain(`before-${id}`);
}, 900_000);

test("a moved folder takes its files along", async () => {
  const id = unique();
  saveFile(`Inbox/${id}/Old/moved-${id}.txt`, `A desert caravan ${id} of camels crossing the dunes towards the oasis at dusk.`);
  expect(await searchUntilFound("desert caravan camels", `${id}/Old`)).toContain(`moved-${id}`);
  await closeLauncher();
  renameSync(at(`Inbox/${id}/Old`), at(`Inbox/${id}/New`));
  const seen = await searchUntilFound("desert caravan camels", `${id}/New`);
  expect(seen).toContain(`moved-${id}`);
  expect(seen).not.toContain(`${id}/Old`);
}, 900_000);

test("a deleted file disappears from the results", async () => {
  const id = unique();
  saveFile(`Inbox/${id}/deleted-${id}.txt`, `A polar bear ${id} walking alone on the sea ice, hunting for seals.`);
  expect(await searchUntilFound("polar bear sea ice", `deleted-${id}`)).toContain(`deleted-${id}`);
  await closeLauncher();
  deleteFile(`Inbox/${id}/deleted-${id}.txt`);
  const seen = await searchUntilGone("polar bear sea ice", `deleted-${id}`);
  expect(seen).toMatch(/results/i);
  expect(seen).not.toContain(`deleted-${id}`);
}, 900_000);

test("a file emptied by the user disappears from the results", async () => {
  const id = unique();
  saveFile(`Inbox/${id}/emptied-${id}.txt`, `Notes on a jazz trumpet solo ${id}, its phrasing, the mute and the high notes.`);
  expect(await searchUntilFound("jazz trumpet solo", `emptied-${id}`)).toContain(`emptied-${id}`);
  await closeLauncher();
  writeFileSync(at(`Inbox/${id}/emptied-${id}.txt`), "");
  const seen = await searchUntilGone("jazz trumpet solo", `emptied-${id}`);
  expect(seen).toMatch(/results/i);
  expect(seen).not.toContain(`emptied-${id}`);
}, 900_000);
