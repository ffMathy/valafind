// ail-derived-from: CommandLine@3fe8fe8b2b4643bf
// End-to-end check for CommandLine: its scenarios, played from a terminal against the running Valafind (with the
// environment it was started with), and, for --quit and for no display, against a Valafind of their own. It sees only
// what a user sees: windows on screen, the command's exit code and what it writes (see app-driver.ts).
import { afterEach, expect } from "bun:test";
import { mkdirSync, rmSync } from "node:fs";
import { join, resolve } from "node:path";
import { closeLauncher, keepScreen, keepScreenIfNone, launcher, sleep, test, unique, waitFor } from "../../desktop.ts";
import { launcherOn, runValafind, showOutput, startOwnValafind, valafindEnv } from "../app-driver.ts";

afterEach(async () => { await closeLauncher(); });

test("--toggle opens the launcher and closes it again", async () => {
  await closeLauncher();
  const first = await runValafind(["--toggle"]);
  expect(first.exitCode).toBe(0);
  expect(await waitFor(launcher, 5000)).toBeTruthy();
  keepScreen();
  const second = await runValafind(["--toggle"]);
  expect(second.exitCode).toBe(0);
  expect(await waitFor(() => !launcher(), 5000)).toBe(true);
}, 60_000);

test("--show opens the launcher", async () => {
  await closeLauncher();
  expect((await runValafind(["--show"])).exitCode).toBe(0);
  expect(await waitFor(launcher, 5000)).toBeTruthy();
  keepScreen();
}, 40_000);

test("starting Valafind again shows the launcher of the copy that runs", async () => {
  await closeLauncher();
  const again = await runValafind([]);
  expect(again.exitCode).toBe(0);
  expect(await waitFor(launcher, 5000)).toBeTruthy();
  keepScreen();
}, 40_000);

test("--background leaves quietly when Valafind already runs", async () => {
  await closeLauncher();
  const quiet = await runValafind(["--background"]);
  expect(quiet.exitCode).toBe(0);
  await sleep(1500);
  expect(launcher()).toBeNull();
  keepScreen();
}, 40_000);

test("--quit ends the copy that runs", async () => {
  const own = await startOwnValafind(["--background"]);
  try {
    const env = { ...valafindEnv(own.home, own.data), DISPLAY: own.display };
    delete env.DBUS_SESSION_BUS_ADDRESS;
    // It runs in the background: --show opens its launcher, once it has started.
    await sleep(3000);
    let shown: string | null = null;
    for (const until = Date.now() + 60_000; !shown && Date.now() < until; await sleep(1000)) {
      await runValafind(["--show"], env, 20_000);
      shown = await waitFor(() => launcherOn(own.display), 3000);
    }
    expect(shown).toBeTruthy();
    keepScreen(own.display);
    const quit = await runValafind(["--quit"], env);
    expect(quit.exitCode).toBe(0);
    const ended = await Promise.race([own.process.exited.then(() => true), sleep(15_000).then(() => false)]);
    expect(ended).toBe(true);
    expect(launcherOn(own.display)).toBeNull();
  } finally {
    keepScreenIfNone(own.display);
    await own.stop();
  }
}, 120_000);

test("without a display Valafind says so and ends", async () => {
  const state = resolve(import.meta.dir, "../../../..", ".ail", `app-check-${unique()}`);
  mkdirSync(join(state, "home"), { recursive: true });
  try {
    const env = valafindEnv(join(state, "home"), join(state, "data"));
    delete env.DISPLAY;
    delete env.WAYLAND_DISPLAY;
    const result = await runValafind([], env);
    const close = await showOutput(`$ valafind\n${result.output}\n$ echo $?\n${result.exitCode}`);
    keepScreen();
    await close();
    expect(result.exitCode).toBe(1);
    expect(result.output).toContain("Valafind could not start its user interface (no graphical session?)");
    expect(result.output).toContain(`Details are in the log folder: ${join(state, "data", "logs")}`);
  } finally {
    rmSync(state, { recursive: true, force: true });
  }
}, 60_000);
