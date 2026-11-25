// End-to-end check for Pacing: the target ticks on the index panel's meters, a limit changed in Settings, and pausing
// and resuming from the launcher's menu. The panel shows only while indexing is under way, so each scenario first
// saves a batch of notes for it to index.
import { afterEach, expect } from "bun:test";
import { closeLauncher, keepScreen, launcher, releaseAll, saveFile, sleep, test, unique } from "../../desktop.ts";
import {
  chooseFromLauncherMenu, find, meterTooltip, openLauncherEmpty, openSettings, pressResume, resultsText, saveSettings,
  searchUntilFound, setSlider,
} from "../indexing-driver.ts";

afterEach(async () => {
  await releaseAll();
  await closeLauncher();
});

/** Saves `count` short notes so indexing has work for a while. */
function giveIndexingWork(id: string, count = 20) {
  for (let i = 1; i <= count; i++)
    saveFile(`Busy/${id}/note-${i}.txt`, `Field notes ${id} number ${i} on bird migration over the marshes, with counts of geese and cranes.`);
}

async function panelShows(): Promise<boolean> {
  for (let i = 0; i < 20; i++) {
    await openLauncherEmpty();
    if (find("Phase 2", launcher())) return true;
    await sleep(500);
  }
  return false;
}

test("the meters mark the targets of the default limits", async () => {
  giveIndexingWork(unique());
  await sleep(3000);
  expect(await panelShows()).toBe(true);
  expect(await meterTooltip("CPU")).toContain("Target 27%");
  expect(await meterTooltip("Disk")).toContain("Target 22%");
}, 180_000);

test("a higher processor limit moves the CPU target", async () => {
  giveIndexingWork(unique());
  try {
    await openSettings();
    await setSlider("Maximum CPU", 50);
    await saveSettings();
    expect(await panelShows()).toBe(true);
    expect(await meterTooltip("CPU")).toContain("Target 45%");
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
    expect(paused).toContain("Indexing paused");
    expect(paused).toMatch(/of your files are searchable/);
    expect(paused).toContain("Paused by you");
    keepScreen();

    saveFile(`Paused/${id}/held-${id}.txt`, `An orchard ${id} of old apple trees, the harvest, the cider press and the ladders.`);
    await closeLauncher();
    await sleep(30_000);
    const meanwhile = await searchUntilFound("apple orchard harvest", `held-${id}`, 5_000);
    expect(meanwhile).not.toContain(`held-${id}`);

    await pressResume();
  } finally {
    // Never leave indexing paused for the other checks: the banner's Resume shows only while paused.
    await openLauncherEmpty();
    if (find("Indexing paused", launcher())) await pressResume();
  }
  await closeLauncher();
  const seen = await searchUntilFound("apple orchard harvest", `held-${id}`);
  expect(seen).toContain(`held-${id}`);
}, 600_000);
