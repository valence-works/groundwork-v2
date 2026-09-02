#!/usr/bin/env python3
"""Verify the published Groundwork wiki product and write a publication-safe evidence record."""

from __future__ import annotations

import argparse
import hashlib
from datetime import UTC, datetime
from html import unescape
from pathlib import Path
from urllib.request import Request, urlopen


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("portal_url")
    parser.add_argument("exact_version")
    parser.add_argument("report", type=Path)
    args = parser.parse_args()

    request = Request(
        args.portal_url,
        headers={"Accept": "text/html", "User-Agent": "groundwork-portal-verifier/1"},
    )
    with urlopen(request, timeout=20) as response:
        body = response.read()
        status = response.status
        final_url = response.geturl()
    html = unescape(body.decode("utf-8", errors="replace"))
    checks = {
        "Published page returned HTTP 2xx": 200 <= status < 300,
        "Exact GroundworkCurrentRelease is visible": args.exact_version in html,
        "Search guidance is visible": "Search" in html,
        "Mobile fallback navigation is visible": "All pages" in html and "small screen" in html,
        "Responsive viewport metadata is present": 'name="viewport"' in html,
        "Code renders with semantic pre/code elements": "<pre" in html and "<code" in html,
        "Edit-source link is visible": "edit portal source" in html,
        "Documentation-feedback link is visible": "report documentation feedback" in html,
    }
    args.report.parent.mkdir(parents=True, exist_ok=True)
    digest = hashlib.sha256(body).hexdigest()
    finished = datetime.now(UTC).strftime("%Y-%m-%dT%H:%M:%SZ")
    rows = [f"| {name} | {'passed' if passed else 'failed'} |" for name, passed in checks.items()]
    args.report.write_text(
        "\n".join(
            [
                "# Groundwork published portal evidence",
                "",
                f"- Requested portal: `{args.portal_url}`",
                f"- Final portal URL: `{final_url}`",
                f"- Exact release: `{args.exact_version}`",
                f"- HTTP status: `{status}`",
                f"- Response SHA-256: `{digest}`",
                f"- Verified (UTC): `{finished}`",
                "",
                "| Product check | Outcome |",
                "| --- | --- |",
                *rows,
                "",
                "The report retains only product-level outcomes and the response hash; the fetched page is not retained.",
                "",
            ]
        ),
        encoding="utf-8",
    )
    failed = [name for name, passed in checks.items() if not passed]
    if failed:
        for name in failed:
            print(f"Published portal check failed: {name}")
        return 1
    print(f"Published portal verified at {args.exact_version}; report: {args.report}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
