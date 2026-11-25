// End-to-end check for Audio: a recording is found by what is said in it, in WAV and, through ffmpeg, other formats;
// a long recording is heard to its end.
import { afterEach, expect } from "bun:test";
import { closeLauncher, releaseAll, test, unique } from "../../desktop.ts";
import { cleanUp, folder, saveFixture, saveWithFfmpeg, searchUntilAbove, shownAbove } from "../kinds-driver.ts";

afterEach(async () => { await releaseAll(); await closeLauncher(); cleanUp(); });

const tone = (path: string) => saveWithFfmpeg(path, ["-f", "lavfi", "-i", "sine=frequency=440:duration=6"]);

test("a recording is found by what is said in it", async () => {
  const dir = folder(), n = unique();
  saveFixture("speech.wav", `${dir}/${n}-a.wav`);
  tone(`${dir}/${n}-b.wav`);
  expect(shownAbove(await searchUntilAbove("a weather forecast", `${n}-a.wav`, [`${n}-b.wav`]), `${n}-a.wav`, `${n}-b.wav`)).toBe(true);
}, 360_000);

test("MP3 and FLAC recordings are read too", async () => {
  const dir = folder(), n = unique();
  saveFixture("speech.mp3", `${dir}/${n}-forecast.mp3`);
  saveWithFfmpeg(`${dir}/${n}-forecast.flac`, ["-i", "fixture:speech.wav"]);
  tone(`${dir}/${n}-tone.wav`);
  for (const name of [`${n}-forecast.mp3`, `${n}-forecast.flac`])
    expect(shownAbove(await searchUntilAbove("a weather forecast", name, [`${n}-tone.wav`]), name, `${n}-tone.wav`)).toBe(true);
}, 480_000);

test("a long recording is heard to its end", async () => {
  const dir = folder(), n = unique();
  // 150 seconds of silence, then the forecast.
  saveWithFfmpeg(`${dir}/${n}-e.wav`, ["-f", "lavfi", "-i", "anullsrc=r=16000:cl=mono", "-i", "fixture:speech.wav",
    "-filter_complex", "[0]atrim=0:150[s];[s][1]concat=n=2:v=0:a=1", "-c:a", "pcm_s16le"]);
  tone(`${dir}/${n}-tone.wav`);
  expect(shownAbove(await searchUntilAbove("a weather forecast", `${n}-e.wav`, [`${n}-tone.wav`]), `${n}-e.wav`, `${n}-tone.wav`)).toBe(true);
}, 480_000);
