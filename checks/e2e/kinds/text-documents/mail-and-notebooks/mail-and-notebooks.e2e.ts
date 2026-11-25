// End-to-end check for MailAndNotebooks: e-mail and mailboxes are found by their headers and bodies, and Jupyter
// notebooks by their cells, without the cells' output.
import { afterEach, expect } from "bun:test";
import { closeLauncher, releaseAll, test, unique } from "../../../desktop.ts";
import { cleanUp, folder, saveDocument, searchUntilFound } from "../../kinds-driver.ts";

afterEach(async () => { await releaseAll(); await closeLauncher(); cleanUp(); });

test("an e-mail is found by its subject and body", async () => {
  const dir = folder(), n = unique(), t = unique();
  saveDocument(`${dir}/${n}.eml`, "eml", ["Dentist appointment", `Your teeth cleaning ${t} is booked for Tuesday at nine.`]);
  const seen = await searchUntilFound("when do I see the dentist", [`${n}.eml`, t]);
  expect(seen).toContain(t);
  expect(seen).toContain("Subject: Dentist appointment");
}, 240_000);

test("every message in a mailbox file is searched", async () => {
  const dir = folder(), n = unique(), t = unique();
  saveDocument(`${dir}/${n}.mbox`, "mbox", ["We should paint the fence green this weekend.", `The chess tournament ${t} starts at noon.`]);
  expect(await searchUntilFound("chess competition", [`${n}.mbox`, t])).toContain(t);
}, 240_000);

test("a notebook is searched by its cells, not their output", async () => {
  const dir = folder(), n = unique(), t = unique(), out = unique();
  saveDocument(`${dir}/${n}.ipynb`, "ipynb", [`# Training ${t}: a neural network that recognises cats`, "model.fit(images, labels)", `accuracy report ${out}`]);
  const seen = await searchUntilFound("teaching a computer to recognise cats", [`${n}.ipynb`, t]);
  expect(seen).toContain(t);
  expect(seen).not.toContain(out);
}, 240_000);
