// ail-derived-from: Pictures@47a3987c5577b814
// End-to-end check for Pictures: a picture is found by what it shows and by the words written in it, in every common
// format; a TIFF (read only on a Mac) is found by its name.
import { afterEach, expect } from "bun:test";
import { closeLauncher, releaseAll, test, unique } from "../../desktop.ts";
import { cleanUp, folder, savePicture, searchUntilAbove, searchUntilFound, shownAbove } from "../kinds-driver.ts";

afterEach(async () => { await releaseAll(); await closeLauncher(); cleanUp(); });

test("a picture is found by what it shows", async () => {
  const dir = folder(), n = unique();
  savePicture(`${dir}/${n}-a.png`, { circle: "red" });
  savePicture(`${dir}/${n}-b.png`, { square: "blue" });
  expect(shownAbove(await searchUntilAbove("a red circle", `${n}-a.png`, [`${n}-b.png`]), `${n}-a.png`, `${n}-b.png`)).toBe(true);
  expect(shownAbove(await searchUntilAbove("a blue square", `${n}-b.png`, [`${n}-a.png`]), `${n}-b.png`, `${n}-a.png`)).toBe(true);
}, 360_000);

test("a picture is found by the words written in it", async () => {
  const dir = folder(), n = unique();
  savePicture(`${dir}/${n}-c.png`, { word: "BANANA" });
  savePicture(`${dir}/${n}-d.png`, { word: "TRACTOR" });
  expect(shownAbove(await searchUntilAbove("a sign that says banana", `${n}-c.png`, [`${n}-d.png`]), `${n}-c.png`, `${n}-d.png`)).toBe(true);
}, 360_000);

test("PNG, JPEG, GIF, BMP and WebP pictures are all read", async () => {
  const dir = folder(), n = unique();
  const circles = ["png", "jpg", "gif", "bmp", "webp"].map((ext) => `${n}-${ext}.${ext}`);
  for (const name of circles) savePicture(`${dir}/${name}`, { circle: "red" });
  savePicture(`${dir}/${n}-square.png`, { square: "blue" });
  for (const name of circles) {
    const seen = await searchUntilAbove("a red circle", name, [`${n}-square.png`]);
    expect(shownAbove(seen, name, `${n}-square.png`)).toBe(true);
  }
}, 600_000);

test("a TIFF picture is found by its name only, except on a Mac", async () => {
  const dir = folder(), n = unique();
  savePicture(`${dir}/lighthouse-${n}.tiff`, { circle: "red" });
  savePicture(`${dir}/${n}-e.png`, { circle: "red" });
  expect(await searchUntilFound("lighthouse", [`lighthouse-${n}.tiff`])).toContain(`lighthouse-${n}.tiff`);
  const seen = await searchUntilAbove("a red circle", `${n}-e.png`, [`lighthouse-${n}.tiff`]);
  expect(shownAbove(seen, `${n}-e.png`, `lighthouse-${n}.tiff`)).toBe(true);
}, 360_000);
