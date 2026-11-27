#!/usr/bin/env python3
"""Puts a GitHub release's notes on the app's pending Microsoft Store submission, then commits it.

store.yml runs this after `msstore publish --noCommit` has uploaded the package into a draft submission:

    release_notes.py --release release.json --product-id 9ABC... [--price-id Tier1102] [--commit]

--release is `gh release view <tag> --json body,url,tagName` (an empty object when there is no release: the notes then
just link to the release page). The notes are set on every listing language: the Store has no translation of them, and
English notes beat last release's. The Store takes plain text of at most 1500 characters, so the Markdown that GitHub
generates ("## What's Changed", "* <title> by @someone in <link>") becomes a short list of what changed.

Signs in with the Partner Center application in TENANT_ID, CLIENT_ID and CLIENT_SECRET (as msstore does).
--print only prints the notes.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

LIMIT = 1500
API = os.environ.get("STORE_API", "https://manage.devcenter.microsoft.com/v1.0/my")
LOGIN = os.environ.get("STORE_LOGIN", "https://login.microsoftonline.com")


def notes_from_release(release: dict, tag: str, repository_url: str) -> str:
    """Plain-text release notes from a GitHub release (its generated Markdown body)."""
    tag = release.get("tagName") or tag
    url = release.get("url") or f"{repository_url}/releases/tag/{tag}"
    version = tag[1:] if tag.startswith("v") else tag
    changes = []
    for line in (release.get("body") or "").splitlines():
        line = line.strip()
        item = re.match(r"^[*-]\s+(.*)$", line)
        if not item:
            continue
        text = item.group(1)
        # GitHub's generated entries end with " by @author in https://github.com/.../pull/N".
        text = re.sub(r"\s+by\s+@[\w-]+(\[bot\])?\s+in\s+\S+\s*$", "", text)
        text = re.sub(r"\s+in\s+https?://\S+\s*$", "", text)
        text = re.sub(r"\[([^\]]+)\]\([^)]+\)", r"\1", text)  # [text](link) -> text
        text = re.sub(r"[`*_]{1,2}([^`*_]+)[`*_]{1,2}", r"\1", text)  # `code`, **bold**, _italic_
        text = text.strip()
        if text:
            changes.append(text)

    head = f"What's new in {version}:" if changes else f"Version {version}."
    tail = f"\n\nAll changes: {url}"
    lines = [head]
    room = LIMIT - len(tail) - len("\n• …and 9999 more")
    for index, change in enumerate(changes):
        line = f"• {change}"
        # Whole entries only; say how many did not fit.
        if len("\n".join(lines + [line])) > room:
            lines.append(f"• …and {len(changes) - index} more")
            break
        lines.append(line)
    return ("\n".join(lines) + tail)[:LIMIT]


def request(method: str, url: str, token: str | None = None, body: dict | None = None, form: dict | None = None) -> dict:
    data, headers = None, {"Accept": "application/json"}
    if token:
        headers["Authorization"] = f"Bearer {token}"
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    if form is not None:
        data = urllib.parse.urlencode(form).encode()
        headers["Content-Type"] = "application/x-www-form-urlencoded"
    try:
        with urllib.request.urlopen(urllib.request.Request(url, data=data, method=method, headers=headers), timeout=120) as r:
            text = r.read().decode()
    except urllib.error.HTTPError as e:
        sys.exit(f"{method} {url.split('?')[0]} failed with {e.code}: {e.read().decode(errors='replace')[:2000]}")
    return json.loads(text) if text.strip() else {}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--release", required=True, help="`gh release view --json body,url,tagName` output")
    parser.add_argument("--tag", default="", help="the release tag, for when there is no release")
    parser.add_argument("--repository-url", default="")
    parser.add_argument("--product-id")
    parser.add_argument("--price-id", default="", help="price tier to keep the submission's price (see store.yml)")
    parser.add_argument("--commit", action="store_true", help="submit the draft for certification afterwards")
    parser.add_argument("--print", action="store_true", help="only print the notes")
    args = parser.parse_args()

    with open(args.release, encoding="utf-8") as f:
        text = f.read().strip()
    notes = notes_from_release(json.loads(text) if text else {}, args.tag, args.repository_url)
    print(notes)
    if args.print:
        return
    if not args.product_id:
        sys.exit("--product-id is needed")

    tenant, client, secret = (os.environ.get(k, "") for k in ("TENANT_ID", "CLIENT_ID", "CLIENT_SECRET"))
    if not (tenant and client and secret):
        sys.exit("TENANT_ID, CLIENT_ID and CLIENT_SECRET are needed")
    token = request("POST", f"{LOGIN}/{tenant}/oauth2/token", form={
        "grant_type": "client_credentials", "client_id": client, "client_secret": secret,
        "resource": "https://manage.devcenter.microsoft.com"})["access_token"]

    app = request("GET", f"{API}/applications/{args.product_id}", token)
    pending = (app.get("pendingApplicationSubmission") or {}).get("id")
    if not pending:
        sys.exit(f"{args.product_id} has no pending submission: msstore publish --noCommit should have made one")
    url = f"{API}/applications/{args.product_id}/submissions/{pending}"
    submission = request("GET", url, token)

    listings = submission.get("listings") or {}
    for listing in listings.values():
        listing.setdefault("baseListing", {})["releaseNotes"] = notes
    # The API reports a price set per market as 'Base', which it rejects on update; an update without a price would
    # make the app free (the same reason store.yml passes --priceId to msstore).
    pricing = submission.setdefault("pricing", {})
    if args.price_id and pricing.get("priceId") in (None, "", "Base"):
        pricing["priceId"] = args.price_id
    if pricing.get("priceId") in (None, "", "Base"):
        sys.exit("The submission's price is set per market: set the MSSTORE_PRICE_ID variable to its tier")

    request("PUT", url, token, body=submission)
    print(f"Release notes set on {len(listings)} listing language(s) of submission {pending}.", file=sys.stderr)

    if args.commit:
        request("POST", f"{url}/commit", token)
        print(f"Submission {pending} sent for certification.", file=sys.stderr)


if __name__ == "__main__":
    main()
