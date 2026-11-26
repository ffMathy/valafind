// Drives the parts of Valafind around the launcher the way a person would: the tray icon and its menu, the settings
// window, starting Valafind from a terminal, and a desktop of its own for a first start. It knows nothing of
// Valafind's code. It needs, on the same DISPLAY as Valafind:
// - a system tray at the bottom of the screen (scripts/ail/tray-host.sh, which scripts/ail/run-legacy.sh starts);
// - scripts/ail/bin first on Valafind's PATH: its xdg-open writes what would have opened to .ail/opened/<display>.txt
//   (desktop.ts reads it with opened()), so no real browser or file manager ever opens.
// Starting Valafind from a terminal uses VALAFIND_COMMAND (the program and its arguments, separated by spaces), by
// default the build folder's Valafind.dll through dotnet, with the environment the running copy was started with:
// its home folder (VALAFIND_HOME), XDG folders inside it, and its data folder (VALAFIND_DATA_DIR, by default "data"
// next to the home folder), as scripts/ail/run-legacy.sh does.
// Positions inside the settings window are those of the window as it opens (580 by 720), in English.
import { existsSync, mkdirSync, rmSync, symlinkSync } from "node:fs";
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { home, sleep, unique, waitFor } from "../desktop.ts";

const root = resolve(import.meta.dir, "../../..");

// tesseract (readScreen) starts a thread per core; on a busy machine they starve each other and one read takes
// minutes. One thread reads a window in under a second.
process.env.OMP_THREAD_LIMIT ??= "1";

function run(...args: string[]): string {
  return Bun.spawnSync(args, { env: process.env, stdout: "pipe", stderr: "pipe" }).stdout.toString();
}
const xdotool = (...args: string[]) => run("xdotool", ...args);
const visibleWindows = (name: string, display = process.env.DISPLAY ?? "") =>
  Bun.spawnSync(["xdotool", "search", "--onlyvisible", "--name", name], { env: { ...process.env, DISPLAY: display }, stdout: "pipe" })
    .stdout.toString().split("\n").filter(Boolean);

/** The absolute position and size of a window's content, without the title bar. */
export function geometry(id: string): { x: number; y: number; width: number; height: number } {
  const info = run("xwininfo", "-id", id);
  const num = (label: string) => Number(new RegExp(`${label}:\\s*(-?\\d+)`).exec(info)?.[1]);
  return { x: num("Absolute upper-left X"), y: num("Absolute upper-left Y"), width: num("Width"), height: num("Height") };
}

const click = async (x: number, y: number, button = "1", after = 300) => {
  xdotool("mousemove", String(Math.round(x)), String(Math.round(y)), "click", button);
  await sleep(after);
};

// ------------------------------------------------------------------ the tray

/** Where Valafind's icon is: the right end of the panel at the bottom of the screen. */
function trayIconPosition(): { x: number; y: number } {
  const panel = visibleWindows("^xfce4-panel$").map(geometry).find((g) => g.width > 200);
  if (!panel) throw new Error("no system tray on this display: start scripts/ail/tray-host.sh, then Valafind with its D-Bus address");
  return { x: panel.x + panel.width - 16, y: panel.y + Math.floor(panel.height / 2) };
}

/** Left-clicks Valafind's tray icon. */
export async function clickTrayIcon() {
  const { x, y } = trayIconPosition();
  await click(x, y, "1", 600);
}

/** The tray menu's items, in order (the separators are skipped by the keyboard). */
export const trayItems = ["Open Valafind", "Pause indexing", "Settings…", "Check for updates…", "Quit Valafind"] as const;
export type TrayItem = (typeof trayItems)[number] | "Resume indexing";

/** Opens the tray menu with a right click and leaves it open. */
export async function openTrayMenu() {
  const { x, y } = trayIconPosition();
  await click(x, y, "3", 1000);
}

/** Opens the tray menu and chooses an item with the arrow keys and Enter. */
export async function chooseFromTrayMenu(item: TrayItem) {
  const index = item === "Resume indexing" ? 1 : trayItems.indexOf(item);
  await openTrayMenu();
  for (let i = 0; i <= index; i++) { xdotool("key", "Down"); await sleep(80); }
  xdotool("key", "Return");
  await sleep(600);
}

/** Closes an open menu or popup with Esc. */
export const escape = async () => { xdotool("key", "Escape"); await sleep(300); };

// ------------------------------------------------------------------ the settings window

export const settingsWindows = () => visibleWindows("^Valafind Settings$");

export async function waitForSettings(open: boolean, timeout = 5000): Promise<boolean> {
  const ok = await waitFor(() => (settingsWindows().length > 0) === open, timeout);
  await sleep(open ? 500 : 0);
  return ok === true;
}

/** Opens the settings from the tray menu, or brings the open window forward. */
export async function openSettings() {
  if (settingsWindows().length === 0) await chooseFromTrayMenu("Settings…");
  if (!(await waitForSettings(true))) throw new Error("the settings window did not open");
  xdotool("windowactivate", "--sync", settingsWindows()[0]!);
  await sleep(300);
}

/** Closes every settings window with Esc, which is the window's Cancel. */
export async function closeSettings() {
  for (const id of settingsWindows()) {
    xdotool("windowactivate", "--sync", id);
    xdotool("key", "Escape");
  }
  await waitForSettings(false);
}

/** Where things are in the settings window, from the top left of its content, as it opens (scrolled to the top)… */
const top = {
  shortcutBox: [257, 74], reset: [511, 73], addFolder: [479, 264], removeFirstFolder: [503, 208],
  cancel: [403, 690], save: [507, 690],
} as const;
/** …and once scrolled to the end. */
const end = {
  dimensions: [479, 121], language: [439, 338], startAtLogin: [518, 395], checkForUpdates: [110, 495], openLogFolder: [477, 608],
  cancel: [403, 690], save: [507, 690],
} as const;
export type TopSpot = keyof typeof top;
export type EndSpot = keyof typeof end;

const settingsOrigin = () => geometry(settingsWindows()[0]!);

export async function clickInSettings(spot: TopSpot) {
  const g = settingsOrigin();
  await click(g.x + top[spot][0], g.y + top[spot][1]);
}

/** Scrolls the settings to the end. */
export async function scrollSettingsToEnd() {
  const g = settingsOrigin();
  xdotool("mousemove", String(g.x + 290), String(g.y + 360));
  for (let i = 0; i < 20; i++) { xdotool("click", "5"); await sleep(30); }
  await sleep(400);
}

/** Scrolls the settings to the end and clicks a spot there. */
export async function clickAtSettingsEnd(spot: EndSpot) {
  await scrollSettingsToEnd();
  const g = settingsOrigin();
  await click(g.x + end[spot][0], g.y + end[spot][1]);
}

/** Records a chord in the shortcut box by pressing it there, e.g. "ctrl+alt+k". */
export async function pressInShortcutBox(keys: string) {
  await clickInSettings("shortcutBox");
  xdotool("key", keys);
  await sleep(300);
}

/** Maximum CPU and Maximum disk busy: their slider from 5 to 100 spans the card, as the window opens. */
const sliders = { cpu: 429, disk: 513 } as const;
const sliderX = (percent: number) => 52 + (percent - 5) * 5;

/** Moves a slider from the value it shows by a number of steps of 5 (negative: down), with the arrow keys. */
export async function moveSlider(which: keyof typeof sliders, from: number, steps: number) {
  const g = settingsOrigin();
  await click(g.x + sliderX(from), g.y + sliders[which]);
  for (let i = 0; i < Math.abs(steps); i++) { xdotool("key", steps > 0 ? "Right" : "Left"); await sleep(150); }
  await sleep(200);
}

/** Picks an entry of an open drop-down, counting from its first entry (each 29.5 high); the language list opens
 * upwards, its first entry 98 above the box, the dimensions list under the box. */
export async function pickFromDropDown(spot: "language" | "dimensions", index: number) {
  const g = settingsOrigin();
  const [x, y] = end[spot];
  const first = spot === "language" ? { dx: -40, dy: -98 } : { dx: -35, dy: 35 };
  await click(g.x + x + first.dx, g.y + y + first.dy + index * 29.5, "1", 600);
}

/** Chooses a folder in the desktop's folder picker: the first folder in the home folder. */
export async function chooseFirstFolderInHome() {
  const dialog = await waitFor(() => visibleWindows("^Choose a folder to index$")[0], 8000);
  if (!dialog) throw new Error("the folder picker did not open");
  await sleep(800);
  const g = geometry(dialog);
  await click(g.x + 57, g.y + 59, "1", 800);    // "Home" in the sidebar
  await click(g.x + 213, g.y + 83, "1", 400);   // the first folder
  await click(g.x + g.width - 141, g.y + g.height - 26, "1", 800);   // "Select"
}

// ------------------------------------------------------------------ the autostart entry

/** The desktop's autostart entry for Valafind in the home folder Valafind runs with. */
export const autostartEntry = () => join(home(), ".config", "autostart", "valafind.desktop");

// ------------------------------------------------------------------ Valafind from a terminal

/** The command that starts Valafind, without options. */
export function valafindCommand(): string[] {
  if (process.env.VALAFIND_COMMAND) return process.env.VALAFIND_COMMAND.split(" ").filter(Boolean);
  const dotnet = [process.env.DOTNET, join(homedir(), ".dotnet", "dotnet")].find((p) => p && existsSync(p)) ?? "dotnet";
  return [dotnet, join(root, "src/Valafind.App/bin/Release/net10.0/Valafind.dll")];
}

/** The environment of a Valafind whose home folder is `homeFolder` and data folder `dataFolder`. */
export function valafindEnv(homeFolder = home(), dataFolder = process.env.VALAFIND_DATA_DIR ?? join(dirname(home()), "data")) {
  return {
    ...process.env,
    HOME: homeFolder, XDG_DATA_HOME: join(homeFolder, ".local/share"), XDG_CONFIG_HOME: join(homeFolder, ".config"),
    VALAFIND_DATA_DIR: resolve(root, dataFolder), VALAFIND_NO_INSTALL: "1", VALAFIND_NO_UPDATE: "1",
    PATH: `${join(root, "scripts/ail/bin")}:${process.env.PATH ?? ""}`,
  } as Record<string, string | undefined>;
}

export type Finished = { exitCode: number; output: string; seconds: number };

/** Runs Valafind with `options` in the environment `env` and waits for that command to end. */
export async function runValafind(options: string[], env = valafindEnv(), timeout = 60_000): Promise<Finished> {
  const started = Date.now();
  const p = Bun.spawn([...valafindCommand(), ...options], { env, stdout: "pipe", stderr: "pipe", cwd: root });
  const timer = setTimeout(() => p.kill(), timeout);
  const [out, err] = await Promise.all([new Response(p.stdout).text(), new Response(p.stderr).text()]);
  const exitCode = await p.exited;
  clearTimeout(timer);
  return { exitCode, output: out + err, seconds: (Date.now() - started) / 1000 };
}

/** Shows what a command wrote, as a terminal would, in a window on this display; close it with the returned function. */
export async function showOutput(text: string): Promise<() => Promise<void>> {
  const dir = join(root, ".ail", `app-out-${unique()}`);
  mkdirSync(dir, { recursive: true });
  const png = join(dir, "out.png");
  Bun.spawnSync(["convert", "-background", "#1e1e1e", "-fill", "#e8e8e8", "-font", "DejaVu-Sans-Mono", "-pointsize", "15",
    "-size", "1180x", `caption:${text.trim()}`, "-bordercolor", "#1e1e1e", "-border", "20", png], { env: process.env });
  const viewer = Bun.spawn(["display", "-title", "Terminal", "-geometry", "+20+20", png], { env: process.env, stdout: "ignore", stderr: "ignore" });
  await waitFor(() => visibleWindows("^Terminal$").length > 0, 5000);
  await sleep(500);
  return async () => { viewer.kill(); await viewer.exited; rmSync(dir, { recursive: true, force: true }); };
}

/** The launcher on a display: a visible window titled "Valafind". */
export const launcherOn = (display: string) => visibleWindows("^Valafind$", display)[0] ?? null;

/** The words on a whole display, read with OCR, with white space made single. */
export function readDisplay(display: string): string {
  const dir = join(root, ".ail", `app-ocr-${unique()}`);
  mkdirSync(dir, { recursive: true });
  const env = { ...process.env, DISPLAY: display };
  Bun.spawnSync(["import", "-window", "root", join(dir, "s.png")], { env });
  Bun.spawnSync(["convert", join(dir, "s.png"), "-resize", "200%", "-colorspace", "Gray", join(dir, "b.png")], { env });
  const text = Bun.spawnSync(["tesseract", join(dir, "b.png"), "stdout", "--psm", "6"], { env: process.env, stdout: "pipe", stderr: "ignore" }).stdout.toString();
  rmSync(dir, { recursive: true, force: true });
  return text.replace(/[“”]/g, '"').replace(/[‘’]/g, "'").replace(/\s+/g, " ").trim();
}

// ------------------------------------------------------------------ a desktop of its own

export type OwnValafind = { display: string; home: string; data: string; process: ReturnType<typeof Bun.spawn>; stop(): Promise<void> };

/** Starts an X display of its own (:248 or :249, whichever is free) and Valafind on it, in a fresh home and data
 * folder, without the model unless `withModel`. */
export async function startOwnValafind(options: string[] = [], withModel = false): Promise<OwnValafind> {
  const number = [248, 249].find((n) => !existsSync(`/tmp/.X${n}-lock`));
  if (!number) throw new Error("no free display among :248 and :249");
  const display = `:${number}`;
  const xvfb = Bun.spawn(["Xvfb", display, "-screen", "0", "1280x800x24", "-nolisten", "tcp"], { stdout: "ignore", stderr: "ignore" });
  await waitFor(() => Bun.spawnSync(["xdpyinfo"], { env: { ...process.env, DISPLAY: display } }).exitCode === 0, 10_000, 100);
  const state = join(root, ".ail", `app-check-${unique()}`);
  const homeFolder = join(state, "home"), data = join(state, "data");
  mkdirSync(join(data, "models"), { recursive: true });
  mkdirSync(homeFolder, { recursive: true });
  if (withModel) installModel(data);
  const env = { ...valafindEnv(homeFolder, data), DISPLAY: display };
  delete env.DBUS_SESSION_BUS_ADDRESS; // no tray on this desktop; never the main desktop's
  const app = Bun.spawn([...valafindCommand(), ...options], { env, stdout: "ignore", stderr: "ignore", cwd: root });
  return {
    display, home: homeFolder, data, process: app,
    async stop() {
      app.kill();
      await Promise.race([app.exited, sleep(5000)]);
      xvfb.kill();
      await Promise.race([xvfb.exited, sleep(3000)]);
      rmSync(state, { recursive: true, force: true });
    },
  };
}

/** Puts the model where a Valafind with data folder `data` looks for it, as running the install command does. */
export function installModel(data: string) {
  const model = resolve(root, process.env.VALAFIND_MODEL_DIR ?? ".model/embeddinggemma-2");
  mkdirSync(join(data, "models"), { recursive: true });
  symlinkSync(model, join(data, "models", "embeddinggemma-2"));
}
