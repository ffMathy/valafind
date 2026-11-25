// ail-derived-from: KindsOfFiles@f4dae5d1f5d9ccb7
// End-to-end check for KindsOfFiles: one question in words finds a recording, a picture and a document by what
// each of them holds, whatever its kind.
import { afterEach, expect } from "bun:test";
import { closeLauncher, releaseAll, saveFile, test, unique } from "../desktop.ts";
import { cleanUp, folder, saveFixture, savePicture, searchUntilAbove, shownAbove } from "./kinds-driver.ts";

afterEach(async () => { await releaseAll(); await closeLauncher(); cleanUp(); });

test("a question finds files of every kind by what they hold", async () => {
  const dir = folder(), n = unique();
  const recording = `${n}-1.wav`, picture = `${n}-2.png`, note = `${n}-3.txt`;
  saveFixture("speech.wav", `${dir}/${recording}`);
  savePicture(`${dir}/${picture}`, { circle: "red" });
  saveFile(`${dir}/${note}`, "Remember to water the tomato plants every morning before work.");

  const garden = await searchUntilAbove("looking after the vegetable garden", note, [recording, picture]);
  expect(shownAbove(garden, note, recording) && shownAbove(garden, note, picture)).toBe(true);
  const circle = await searchUntilAbove("a red circle", picture, [recording, note]);
  expect(shownAbove(circle, picture, recording) && shownAbove(circle, picture, note)).toBe(true);
  const weather = await searchUntilAbove("a weather forecast", recording, [picture, note]);
  expect(shownAbove(weather, recording, picture) && shownAbove(weather, recording, note)).toBe(true);
}, 600_000);
