// End-to-end check for TextSniffing: a file without a known extension is read when it looks like text, in whatever
// encoding it was written, and left out when it does not.
import { afterEach, expect } from "bun:test";
import { randomBytes } from "node:crypto";
import { closeLauncher, keepScreen, readScreen, releaseAll, saveFile, search, test, unique } from "../../desktop.ts";
import { cleanUp, folder, searchUntilFound } from "../kinds-driver.ts";

afterEach(async () => { await releaseAll(); await closeLauncher(); cleanUp(); });

test("a text file without an extension is read", async () => {
  const dir = folder(), n = unique(), t = unique();
  saveFile(`${dir}/${n}-CHANGELOG`, `Release ${t}: fixed the crash when the printer ran out of paper during a long print job.\n`);
  expect(await searchUntilFound("the printer ran out of paper", [`${n}-CHANGELOG`, t])).toContain(t);
}, 240_000);

test("a UTF-16 text file without a byte order mark is read", async () => {
  const dir = folder(), n = unique(), t = unique();
  saveFile(`${dir}/${n}.log`, Buffer.from(`Night ${t}: the greenhouse heater switched on because frost was coming.`, "utf16le"));
  expect(await searchUntilFound("frost in the greenhouse", [`${n}.log`, t])).toContain(t);
}, 240_000);

test("a binary file with an unknown extension is not indexed", async () => {
  const dir = folder(), n = unique(), t = unique();
  saveFile(`${dir}/sailing-logbook-${n}.bin`, randomBytes(4000));
  saveFile(`${dir}/sailing-notes-${n}.txt`, `Sailing ${t}: we passed the lighthouse at noon with a fair wind.`);
  // The text file was saved after the binary one: once it is found, the binary one has been looked at too.
  await searchUntilFound("sailing logbook", [`sailing-notes-${n}.txt`]);
  await search(`sailing logbook ${n}`);
  const seen = readScreen();
  keepScreen();
  expect(seen).not.toContain(`sailing-logbook-${n}`);
}, 240_000);
