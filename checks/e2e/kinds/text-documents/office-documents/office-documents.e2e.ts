// End-to-end check for OfficeDocuments: Word, Excel, PowerPoint, OpenDocument, EPUB, RTF and PDF files are found by
// their text, and the passage that matched is shown.
import { afterEach, expect } from "bun:test";
import { closeLauncher, releaseAll, test, unique } from "../../../desktop.ts";
import { cleanUp, folder, saveDocument, searchUntilFound } from "../../kinds-driver.ts";

afterEach(async () => { await releaseAll(); await closeLauncher(); cleanUp(); });

async function foundByItsText(format: string, paragraphs: (t: string) => string[], query: string) {
  const dir = folder(), n = unique(), t = unique();
  saveDocument(`${dir}/${n}.${format}`, format, paragraphs(t));
  expect(await searchUntilFound(query, [`${n}.${format}`, t])).toContain(t);
}

test("a Word document is found by its text", () =>
  foundByItsText("docx", (t) => [`Beekeeping log ${t}`, "The beekeeper harvested honey from twelve hives this autumn."], "collecting honey from bees"), 240_000);

test("an Excel workbook is found by its sheet names and cells", () =>
  foundByItsText("xlsx", (t) => ["Telescope budget", `Mirror grinding ${t}`, "Observatory telescope repairs cost 900 kroner"], "what the telescope repairs cost"), 240_000);

test("a PowerPoint presentation is found by its slides and speaker notes", () =>
  foundByItsText("pptx", (t) => ["Quarterly review", "Agenda", `Remind everyone ${t} to bring ash masks for the volcano field trip`], "volcano field trip"), 240_000);

test("an OpenDocument text is found by its text", () =>
  foundByItsText("odt", (t) => [`Recipe ${t}`, "Sourdough bread with a long cold fermentation."], "baking bread"), 240_000);

test("an EPUB book is found by its chapters", () =>
  foundByItsText("epub", (t) => ["Chapter one: the pirate ship sailed into the harbour at dawn.", `Chapter two ${t}: the treasure map.`], "pirates looking for treasure"), 240_000);

test("an RTF document is found by its text", () =>
  foundByItsText("rtf", (t) => [`Pattern ${t}`, "How to knit a wool scarf for the winter."], "knitting a scarf"), 240_000);

test("a PDF is found by its text", () =>
  foundByItsText("pdf", (t) => [`Workshop ${t}`, "How to change a flat bicycle tyre."], "fixing a punctured bike wheel"), 240_000);
