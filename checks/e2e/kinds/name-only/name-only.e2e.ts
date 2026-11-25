// ail-derived-from: NameOnly@ec098ee66742baa3
// End-to-end check for NameOnly: archives, legacy Office files and media no decoder can read are found by their name,
// and what is inside them never shows.
import { afterEach, expect } from "bun:test";
import { randomBytes } from "node:crypto";
import { closeLauncher, releaseAll, saveFile, test, unique } from "../../desktop.ts";
import { cleanUp, folder, searchUntilFound } from "../kinds-driver.ts";

afterEach(async () => { await releaseAll(); await closeLauncher(); cleanUp(); });

test("an archive is found by its name, not by what is in it", async () => {
  const dir = folder(), n = unique(), t = unique();
  const path = saveFile(`${dir}/holiday-photos-from-crete-${n}.zip`, "");
  const r = Bun.spawnSync(["python3", "-I", "-c", "import sys, zipfile; zipfile.ZipFile(sys.argv[1], 'w').writestr('inside.txt', sys.argv[2])",
    path, `The submarine sonar heard a whale song ${t}.`]);
  expect(r.exitCode).toBe(0);
  const seen = await searchUntilFound("holiday photos from Crete", [`holiday-photos-from-crete-${n}.zip`]);
  expect(seen).not.toContain(t);
}, 420_000);

test("an old Word file is found by its name only", async () => {
  const dir = folder(), n = unique(), t = unique();
  saveFile(`${dir}/minutes-of-the-board-meeting-${n}.doc`, `The elephant parade ${t} passed the castle gates.`);
  const seen = await searchUntilFound("board meeting minutes", [`minutes-of-the-board-meeting-${n}.doc`]);
  expect(seen).not.toContain(t);
}, 420_000);

test("a video no decoder can read is still found by its name", async () => {
  const dir = folder(), n = unique();
  saveFile(`${dir}/sailing-trip-to-norway-${n}.mp4`, randomBytes(4000));
  expect(await searchUntilFound("sailing trip to Norway", [`sailing-trip-to-norway-${n}.mp4`])).toContain(`sailing-trip-to-norway-${n}.mp4`);
}, 420_000);
