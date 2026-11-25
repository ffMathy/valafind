// End-to-end check for Pacing: the target ticks on the index panel's meters, a limit changed in Settings, and pausing
// and resuming from the launcher's menu. The panel shows only while indexing is under way, so each scenario first
// saves a batch of notes for it to index.
import { afterEach, expect } from "bun:test";
import { closeLauncher, keepScreen, launcher, releaseAll, saveFile, sleep, test, unique } from "../../desktop.ts";
import {
  chooseFromLauncherMenu, indexPanelShown, meterTooltip, openLauncherEmpty, openSettings, pressResume, resultsText, saveSettings,
  listed, searchUntilFound, setSlider,
} from "../indexing-driver.ts";

afterEach(async () => {
  await releaseAll();
  await closeLauncher();
});

/** Saves `count` long notes (several parts each) so indexing has work for a while. */
function giveIndexingWork(id: string, count = 12) {
  for (let i = 1; i <= count; i++) {
    const lines = Array.from({ length: 40 }, (_, n) =>
      `Day ${n + 1}: field notes ${id}-${i} on bird migration over the marshes, counting ${n * 7 + i} geese, cranes and lapwings.`);
    saveFile(`Busy/${id}/note-${i}.txt`, lines.join("\n"));
  }
}

async function panelShows(): Promise<boolean> {
  for (let i = 0; i < 20; i++) {
    await openLauncherEmpty();
    if (indexPanelShown()) return true;
    await sleep(500);
  }
  return false;
}

test("the meters mark the targets of the default limits", async () => {
  giveIndexingWork(unique());
  expect(await panelShows()).toBe(true);
  expect(listed(await meterTooltip("CPU"), "Target 27%")).toBe(true);
  expect(listed(await meterTooltip("Disk"), "Target 22%")).toBe(true);
}, 180_000);

test("a higher processor limit moves the CPU target", async () => {
  giveIndexingWork(unique());
  try {
    await openSettings();
    await setSlider("Maximum CPU", 50);
    await saveSettings();
    expect(await panelShows()).toBe(true);
    expect(listed(await meterTooltip("CPU"), "Target 45%")).toBe(true);
  } finally {
    await closeLauncher();
    await openSettings();
    await setSlider("Maximum CPU", 30);
    await saveSettings();
  }
}, 180_000);

test("pausing from the launcher's menu holds new files back until the user resumes", async () => {
  const id = unique();
  try {
    await chooseFromLauncherMenu("Pause indexing");
    await sleep(1000);
    const paused = resultsText();
    expect(listed(paused, "Indexing paused")).toBe(true);
    expect(paused).toMatch(/of your files are searchable/);
    expect(listed(paused, "Paused by you")).toBe(true);
    keepScreen();

    saveFile(`Paused/${id}/held-${id}.txt`, `An orchard ${id} of old apple trees, the harvest, the cider press and the ladders.`);
    await closeLauncher();
    await sleep(30_000);
    const meanwhile = await searchUntilFound(`apple orchard ${id} harvest`, `held-${id}`, 5_000);
    expect(listed(meanwhile, `held-${id}`)).toBe(false);

    await openLauncherEmpty();
    await pressResume();
  } finally {
    // Never leave indexing paused for the other checks: the banner's Resume shows only while paused.
    await openLauncherEmpty();
    if (/Indexing paused/.test(resultsText())) await pressResume();
  }
  await closeLauncher();
  const seen = await searchUntilFound(`apple orchard ${id} harvest`, `held-${id}`);
  expect(listed(seen, `held-${id}`)).toBe(true);
}, 600_000);
