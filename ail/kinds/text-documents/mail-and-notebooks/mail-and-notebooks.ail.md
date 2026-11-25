# E-mail and notebooks

Saved e-mail (.eml), mailboxes (.mbox) and Jupyter notebooks (.ipynb) are found by what they say: a message by its
headers and body, a notebook by its cells.

```ail
node MailAndNotebooks : flow   level: experience
  why   An agreement is often only in an e-mail ("where did we negotiate the deposit?"), and an analysis only in a
        notebook; both must be found by what they say.   <- TextDocuments
  what
    A message gives a short header block (Subject, From, To and Date, each on its own line), a blank line, then its
      body: the plain text when it has one, otherwise its web-page body as text. Attachments are not read.
    A mailbox gives its messages one after another, up to 500 messages or the 32,000 characters.
    Outlook's .msg files are found by their name only.
    A notebook gives the text of its Markdown, code and raw cells in order, a blank line apart; what the cells
      printed or drew is left out.
    Scenario: an e-mail is found by its subject and body
      Given an e-mail "Dentist appointment" that says "Your teeth cleaning <code> is booked for Tuesday at nine."
      When the user searches for "when do I see the dentist"
      Then the e-mail is listed with a passage that starts "Subject: Dentist appointment" and holds the code
    Scenario: every message in a mailbox file is searched
      Given a mailbox whose first message says "We should paint the fence green this weekend." and whose second
        says "The chess tournament <code> starts at noon."
      When the user searches for "chess competition"
      Then the mailbox is listed with the second message's passage
    Scenario: a notebook is searched by its cells, not their output
      Given a notebook with a Markdown cell "# Training <code>: a neural network that recognises cats", a code cell
        "model.fit(images, labels)" and that cell's output "accuracy report <other code>"
      When the user searches for "teaching a computer to recognise cats"
      Then the notebook is listed with its passage
      And the output's code is not shown anywhere
  how
    decide the header block comes first, in the order Subject, From, To, Date
      because people search for a mail by its subject and who sent it as much as by its body (MailExtractor)
    decide plain text before the web-page body
      because the web-page body of a mail is mostly layout, and its text is the same (MailExtractor)
    decide attachments are not read
      because "unknown, confirm with the owner: should attachments of a saved e-mail be searchable?"
    decide notebook outputs are left out
      because they are often large, binary or noisy (NotebookExtractor)
  uses      FormatRules
  sources
    README.md, "What gets indexed" (e-mail .eml, .mbox and Jupyter notebooks)
    driven on Linux (Xvfb, openbox) 2026-11-25: an .eml, an .mbox and an .ipynb made with checks/e2e/kinds/make-file.py, saved and searched
    src/Valafind.Core/Extraction/MailExtractor.cs, NotebookExtractor.cs, MetadataOnlyExtractor.cs (.msg)
    tests/Valafind.Core.Tests/Extraction/DocumentReaderTests.cs, Eml_HeadersAndPlainBody, Eml_HtmlOnlyBody_IsConverted, Mbox_ConcatenatesMessages, Msg_IsMetadataOnly, Notebook_SourcesWithoutOutputs
```
