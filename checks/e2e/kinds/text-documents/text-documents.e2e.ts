// ail-derived-from: TextDocuments@4ae9fc77f758832a
// End-to-end check for TextDocuments: plain text, Markdown, code and web pages are found by what they say, and only
// the first part of a long document is searched.
import { afterEach, expect } from "bun:test";
import { closeLauncher, keepScreen, readScreen, releaseAll, saveFile, search, test, unique } from "../../desktop.ts";
import { cleanUp, filler, folder, searchUntilFound } from "../kinds-driver.ts";

afterEach(async () => { await releaseAll(); await closeLauncher(); cleanUp(); });

test("a text file is found by what it says, with the passage that matched", async () => {
  const dir = folder(), n = unique(), t = unique();
  saveFile(`${dir}/${n}.txt`, `Logbook ${t}: the lighthouse keeper repaired the foghorn before the winter storm arrived.`);
  const seen = await searchUntilFound("who looks after the lighthouse", [`${n}.txt`, t]);
  expect(seen).toContain(t);
}, 420_000);

test("Markdown and code are read as text", async () => {
  const dir = folder(), n = unique(), t = unique(), c = unique();
  saveFile(`${dir}/${n}.md`, `# Packing list ${t}\n\n- tent\n- sleeping bag\n- a torch for the camping trip\n`);
  saveFile(`${dir}/${n}.py`, `def to_celsius(fahrenheit):\n    """Convert a temperature from Fahrenheit to Celsius ${c}."""\n    return (fahrenheit - 32) * 5 / 9\n`);
  expect(await searchUntilFound("what to bring when camping", [`${n}.md`, t])).toContain(t);
  expect(await searchUntilFound("temperature conversion", [`${n}.py`, c])).toContain(c);
}, 420_000);

test("a web page is read without its markup", async () => {
  const dir = folder(), n = unique(), t = unique();
  saveFile(`${dir}/${n}.html`, `<html><head><style>.glow { color: crimson }</style><script>var trackingPixel = 1;</script></head>` +
    `<body><h1>Aquarium visit ${t}</h1><p>The jellyfish tank glows blue in the dark.</p></body></html>`);
  const seen = await searchUntilFound("jellyfish at the aquarium", [`${n}.html`, t]);
  expect(seen).toContain(t);
  expect(seen).not.toContain("trackingPixel");
  expect(seen).not.toContain("crimson");
}, 420_000);

test("only the first part of a long document is searched", async () => {
  const dir = folder(), n = unique(), tail = unique(), control = unique();
  saveFile(`${dir}/${n}-long.txt`, `${filler(40_000)}\nThe astronaut floated outside the space station to repair a solar panel, mission ${tail}.\n`);
  saveFile(`${dir}/${n}-short.txt`, `Our cat ${control} sleeps on the warm windowsill all afternoon.`);
  // The short file was saved after the long one: once it is found, the long one has been read too.
  await searchUntilFound("a cat sleeping in the sun", [`${n}-short.txt`, control]);
  await search("an astronaut repairing a solar panel");
  const seen = readScreen();
  keepScreen();
  expect(seen).not.toContain(tail);
}, 420_000);
