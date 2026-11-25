// End-to-end check for WhatGetsIndexed: which files in the home folder (and in folders added in Settings) are found,
// and which are skipped. It only saves files as a user would, uses Settings with the mouse and keys, and reads the
// launcher from the screen.
import { afterEach, expect } from "bun:test";
import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { closeLauncher, home, releaseAll, saveFile, test, unique } from "../../desktop.ts";
import {
  addFolder, openSettings, removeAllFolders, saveSettings, searchUntilFound, searchUntilGone, listed,
} from "../indexing-driver.ts";

afterEach(async () => {
  await releaseAll();
  await closeLauncher();
});

const recipe = (id: string) => `A recipe for blueberry pancakes ${id} with maple syrup and a pinch of cinnamon.`;

test("a file saved anywhere in the home folder is found", async () => {
  const id = unique();
  saveFile(`Recipes/${id}/pancakes-${id}.txt`, recipe(id));
  const seen = await searchUntilFound("blueberry pancakes recipe", `pancakes-${id}`);
  expect(listed(seen, `pancakes-${id}`)).toBe(true);
  expect(listed(seen, `Recipes/${id}`)).toBe(true);
}, 480_000);

test("hidden folders, installed packages and version control are skipped", async () => {
  const id = unique();
  saveFile(`.hidden-${id}/hidden-${id}.txt`, recipe(id));
  saveFile(`node_modules/${id}/package-${id}.txt`, recipe(id));
  saveFile(`.git/${id}/git-${id}.txt`, recipe(id));
  saveFile(`Kitchen/${id}/kept-${id}.txt`, recipe(id));
  const seen = await searchUntilFound(`blueberry pancakes ${id}`, `kept-${id}`);
  expect(listed(seen, `kept-${id}`)).toBe(true);
  expect(listed(seen, `hidden-${id}`)).toBe(false);
  expect(listed(seen, `package-${id}`)).toBe(false);
  expect(listed(seen, `git-${id}`)).toBe(false);
}, 480_000);

test("build output next to a project file is skipped, a folder of the same name elsewhere is not", async () => {
  const id = unique();
  saveFile(`Web-${id}/package.json`, `{ "name": "web-${id}" }`);
  saveFile(`Web-${id}/dist/built-${id}.txt`, recipe(id));
  saveFile(`Notes-${id}/dist/notes-${id}.txt`, recipe(id));
  const seen = await searchUntilFound(`blueberry pancakes ${id}`, `notes-${id}`);
  expect(listed(seen, `notes-${id}`)).toBe(true);
  expect(listed(seen, `built-${id}`)).toBe(false);
}, 480_000);

test("a folder added in Settings is indexed along with the home folder", async () => {
  const id = unique();
  // A folder next to the home folder, outside it, as a second drive or a shared folder would be.
  const shelf = resolve(home(), "..", `Shelf-${id}`);
  mkdirSync(shelf, { recursive: true });
  writeFileSync(join(shelf, `shelf-${id}.txt`), `Notes about the old stone lighthouse ${id} on the northern cliffs and its keeper.`);
  try {
    await openSettings();
    await addFolder(home());
    await addFolder(shelf);
    await saveSettings();
    const seen = await searchUntilFound("old stone lighthouse keeper", `shelf-${id}`);
    expect(listed(seen, `shelf-${id}`)).toBe(true);
    await closeLauncher();

    await openSettings();
    await removeAllFolders();
    await saveSettings();
    const after = await searchUntilGone("old stone lighthouse keeper", `shelf-${id}`);
    expect(listed(after, `shelf-${id}`)).toBe(false);
  } finally {
    // Leave Settings as they start: no folders listed, so the home folder is indexed.
    try {
      await closeLauncher();
      await openSettings();
      await removeAllFolders();
      await saveSettings();
    } catch {
      // Already as they start.
    }
    rmSync(shelf, { recursive: true, force: true });
  }
}, 1_000_000);
