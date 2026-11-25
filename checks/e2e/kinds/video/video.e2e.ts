// End-to-end check for Video: a video is found by what its pictures show and by what is said in it.
import { afterEach, expect } from "bun:test";
import { closeLauncher, releaseAll, test, unique } from "../../desktop.ts";
import { cleanUp, folder, savePicture, saveWithFfmpeg, searchUntilAbove, shownAbove } from "../kinds-driver.ts";

afterEach(async () => { await releaseAll(); await closeLauncher(); cleanUp(); });

/** A 3-second video of a still picture, without sound. */
function stillVideo(path: string, picture: string) {
  saveWithFfmpeg(path, ["-loop", "1", "-i", `home:${picture}`, "-t", "3", "-r", "10", "-pix_fmt", "yuv420p"]);
}

test("a video is found by what its pictures show", async () => {
  const dir = folder(), n = unique();
  // The pictures are saved under names that sort after the videos, so they never stand in for them.
  savePicture(`${dir}/z-${n}-circle.png`, { circle: "red" });
  savePicture(`${dir}/z-${n}-square.png`, { square: "blue" });
  stillVideo(`${dir}/${n}-a.mp4`, `${dir}/z-${n}-circle.png`);
  stillVideo(`${dir}/${n}-b.mp4`, `${dir}/z-${n}-square.png`);
  expect(shownAbove(await searchUntilAbove("a red circle", `${n}-a.mp4`, [`${n}-b.mp4`]), `${n}-a.mp4`, `${n}-b.mp4`)).toBe(true);
}, 480_000);

test("a video is found by what is said in it", async () => {
  const dir = folder(), n = unique();
  const black = ["-f", "lavfi", "-i", "color=c=black:s=320x240:r=10"];
  saveWithFfmpeg(`${dir}/${n}-c.mp4`, [...black, "-i", "fixture:speech.wav", "-shortest", "-pix_fmt", "yuv420p", "-c:a", "aac"]);
  saveWithFfmpeg(`${dir}/${n}-d.mp4`, [...black, "-f", "lavfi", "-i", "sine=frequency=440:duration=6", "-shortest", "-pix_fmt", "yuv420p", "-c:a", "aac"]);
  expect(shownAbove(await searchUntilAbove("a weather forecast", `${n}-c.mp4`, [`${n}-d.mp4`]), `${n}-c.mp4`, `${n}-d.mp4`)).toBe(true);
}, 480_000);
