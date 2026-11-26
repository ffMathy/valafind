# Writes a small document of one kind, the way the app that makes such files would save it, for the end-to-end checks
# of what Valafind indexes. Usage: python3 -I make-file.py <format> <output path> <json: list of paragraphs>
# Formats: docx, xlsx, pptx, odt, epub, rtf, pdf, eml, mbox, ipynb. It uses only Python's standard library.
import json
import sys
import zipfile
from xml.sax.saxutils import escape

fmt, out, paragraphs = sys.argv[1], sys.argv[2], json.loads(sys.argv[3])
R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
PR = "http://schemas.openxmlformats.org/package/2006/relationships"


def write_zip(entries):
    with zipfile.ZipFile(out, "w") as z:
        for name, content in entries:
            kind = zipfile.ZIP_STORED if name == "mimetype" else zipfile.ZIP_DEFLATED
            z.writestr(name, content, compress_type=kind)


def rels(*items):
    body = "".join(f'<Relationship Id="{i}" Type="{t}" Target="{target}"/>' for i, t, target in items)
    return f'<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="{PR}">{body}</Relationships>'


def docx():
    body = "".join(f"<w:p><w:r><w:t xml:space=\"preserve\">{escape(p)}</w:t></w:r></w:p>" for p in paragraphs)
    write_zip([
        ("[Content_Types].xml", '<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>'),
        ("_rels/.rels", rels(("rId1", f"{R}/officeDocument", "word/document.xml"))),
        ("word/document.xml", '<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">'
                              f"<w:body>{body}</w:body></w:document>"),
    ])


def xlsx():
    # The first paragraph names the sheet; each further one is a row of one cell.
    rows = "".join(f'<row r="{n}"><c r="A{n}" t="inlineStr"><is><t>{escape(p)}</t></is></c></row>' for n, p in enumerate(paragraphs[1:], 1))
    write_zip([
        ("_rels/.rels", rels(("rId1", f"{R}/officeDocument", "xl/workbook.xml"))),
        ("xl/workbook.xml", f'<?xml version="1.0"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="{R}">'
                            f'<sheets><sheet name="{escape(paragraphs[0])}" sheetId="1" r:id="rId1"/></sheets></workbook>'),
        ("xl/_rels/workbook.xml.rels", rels(("rId1", f"{R}/worksheet", "worksheets/sheet1.xml"))),
        ("xl/worksheets/sheet1.xml", f'<?xml version="1.0"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>{rows}</sheetData></worksheet>'),
    ])


def pptx():
    # One slide per paragraph, except the last, which is the first slide's speaker notes.
    *slides, notes = paragraphs
    a = 'xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"'

    def shape(text):
        return f'<p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>{escape(text)}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld>'

    ids = "".join(f'<p:sldId id="{256 + n}" r:id="rId{n + 1}"/>' for n in range(len(slides)))
    entries = [
        ("_rels/.rels", rels(("rId1", f"{R}/officeDocument", "ppt/presentation.xml"))),
        ("ppt/presentation.xml", f'<?xml version="1.0"?><p:presentation {a} xmlns:r="{R}"><p:sldIdLst>{ids}</p:sldIdLst></p:presentation>'),
        ("ppt/_rels/presentation.xml.rels", rels(*[(f"rId{n + 1}", f"{R}/slide", f"slides/slide{n + 1}.xml") for n in range(len(slides))])),
        ("ppt/slides/_rels/slide1.xml.rels", rels(("rId1", f"{R}/notesSlide", "../notesSlides/notesSlide1.xml"))),
        ("ppt/notesSlides/notesSlide1.xml", f'<?xml version="1.0"?><p:notes {a}>{shape(notes)}</p:notes>'),
    ]
    entries += [(f"ppt/slides/slide{n + 1}.xml", f'<?xml version="1.0"?><p:sld {a}>{shape(s)}</p:sld>') for n, s in enumerate(slides)]
    write_zip(entries)


def odt():
    body = "".join(f"<text:p>{escape(p)}</text:p>" for p in paragraphs)
    write_zip([
        ("mimetype", "application/vnd.oasis.opendocument.text"),
        ("content.xml", '<?xml version="1.0" encoding="UTF-8"?><office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" '
                        'xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0">'
                        f"<office:body><office:text>{body}</office:text></office:body></office:document-content>"),
    ])


def epub():
    # One chapter per paragraph, in spine order.
    items = "".join(f'<item id="c{n}" href="c{n}.xhtml" media-type="application/xhtml+xml"/>' for n in range(len(paragraphs)))
    spine = "".join(f'<itemref idref="c{n}"/>' for n in range(len(paragraphs)))
    entries = [
        ("mimetype", "application/epub+zip"),
        ("META-INF/container.xml", '<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">'
                                   '<rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>'),
        ("OEBPS/content.opf", f'<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="3.0"><manifest>{items}</manifest><spine>{spine}</spine></package>'),
    ]
    entries += [(f"OEBPS/c{n}.xhtml", f'<html xmlns="http://www.w3.org/1999/xhtml"><body><p>{escape(p)}</p></body></html>') for n, p in enumerate(paragraphs)]
    write_zip(entries)


def rtf():
    body = "".join(p.replace("\\", "\\\\").replace("{", "\\{").replace("}", "\\}") + "\\par " for p in paragraphs)
    with open(out, "w", encoding="ascii") as f:
        f.write("{\\rtf1\\ansi\\ansicpg1252\\deff0{\\fonttbl{\\f0\\fswiss Helvetica;}}{\\colortbl;\\red255\\green0\\blue0;}\\pard\\f0\\fs24 " + body + "}")


def pdf():
    # One page; each paragraph is a line of Helvetica.
    lines = "".join(f"BT /F1 12 Tf 40 {750 - 16 * n} Td ({p.replace(chr(92), chr(92) * 2).replace('(', chr(92) + '(').replace(')', chr(92) + ')')}) Tj ET\n"
                    for n, p in enumerate(paragraphs))
    objects = [
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
        f"<< /Length {len(lines.encode('latin-1'))} >>\nstream\n{lines}endstream",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
    ]
    data, offsets = b"%PDF-1.4\n", []
    for n, o in enumerate(objects, 1):
        offsets.append(len(data))
        data += f"{n} 0 obj\n{o}\nendobj\n".encode("latin-1")
    xref = len(data)
    data += f"xref\n0 {len(objects) + 1}\n0000000000 65535 f \n".encode()
    data += "".join(f"{o:010d} 00000 n \n" for o in offsets).encode()
    data += f"trailer\n<< /Size {len(objects) + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n".encode()
    open(out, "wb").write(data)


def message(subject, body):
    return (f"From: Alice Example <alice@example.com>\nTo: Bob <bob@example.com>\nSubject: {subject}\n"
            "Date: Fri, 01 May 2026 10:30:00 +0000\nMIME-Version: 1.0\nContent-Type: text/plain; charset=utf-8\n\n" + body + "\n")


def eml():
    # The first paragraph is the subject, the rest the body.
    open(out, "w", encoding="utf-8").write(message(paragraphs[0], "\n\n".join(paragraphs[1:])))


def mbox():
    # One message per paragraph, each with its paragraph as the body.
    with open(out, "w", encoding="utf-8") as f:
        for n, p in enumerate(paragraphs, 1):
            f.write("From alice@example.com Fri May  1 10:30:00 2026\n" + message(f"Message {n}", p) + "\n")


def ipynb():
    # The first paragraph is a markdown cell, the second a code cell whose output is the third.
    cells = [
        {"cell_type": "markdown", "metadata": {}, "source": [paragraphs[0]]},
        {"cell_type": "code", "execution_count": 1, "metadata": {}, "source": paragraphs[1],
         "outputs": [{"output_type": "stream", "name": "stdout", "text": [paragraphs[2]]}]},
    ]
    json.dump({"cells": cells, "metadata": {}, "nbformat": 4, "nbformat_minor": 5}, open(out, "w", encoding="utf-8"))


{"docx": docx, "xlsx": xlsx, "pptx": pptx, "odt": odt, "epub": epub, "rtf": rtf, "pdf": pdf,
 "eml": eml, "mbox": mbox, "ipynb": ipynb}[fmt]()
