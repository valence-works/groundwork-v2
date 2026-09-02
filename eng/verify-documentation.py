#!/usr/bin/env python3
"""Verify the repository's Markdown links, anchors, and executable snippet manifest."""

from __future__ import annotations

import argparse
import json
import re
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable
from urllib.error import HTTPError, URLError
from urllib.parse import unquote, urlparse
from urllib.request import Request, urlopen


ALLOWED_LANGUAGES = frozenset({"bash", "csharp", "json", "markdown", "text", "xml", "yaml"})
ALLOWED_MODES = frozenset({"feedz", "local"})
RETRYABLE_STATUS_CODES = frozenset({408, 425, 429}) | frozenset(range(500, 600))
GET_ONLY_HOSTS = frozenset({"f.feedz.io"})
LINK_PATTERN = re.compile(r"(?<!\!)(?:\[[^\]]*\]|<(?P<autolink>https?://[^>]+)>)\((?P<target>[^\s)]*)\)|(?P<bare><https?://[^>]+>)")
PLAIN_URL_PATTERN = re.compile(r"(?<![<(\[\"'])https?://[^\s<>()]+")
HEADING_PATTERN = re.compile(r"^ {0,3}#{1,6}\s+(.+?)\s*#*\s*$")
HTML_ANCHOR_PATTERN = re.compile(r"<(?:a|span)\s+[^>]*?(?:id|name)=[\"']([^\"']+)[\"'][^>]*>", re.IGNORECASE)


@dataclass(frozen=True)
class Finding:
    source: str
    line: int
    target: str
    reason: str

    def format(self) -> str:
        return f"{self.source}:{self.line}: {self.target!r}: {self.reason}"


def markdown_files(root: Path) -> list[Path]:
    candidates: set[Path] = set()
    readme = root / "README.md"
    if readme.is_file():
        candidates.add(readme)
    for directory in (root / "docs", root / "samples"):
        if directory.is_dir():
            candidates.update(path for path in directory.rglob("*.md") if path.is_file())
    return sorted(candidates, key=lambda path: path.relative_to(root).as_posix())


def source_label(root: Path, path: Path) -> str:
    return path.relative_to(root).as_posix()


def unescape_target(target: str) -> str:
    target = target.strip().strip("<>")
    if target.startswith("<") and target.endswith(">"):
        target = target[1:-1]
    return target.replace("\\(", "(").replace("\\)", ")")


def slugify_heading(text: str) -> str:
    text = re.sub(r"<[^>]+>", "", text)
    text = re.sub(r"[`*_~]", "", text)
    text = text.casefold()
    text = re.sub(r"[^\w\s-]", "", text, flags=re.UNICODE)
    return re.sub(r"[\s-]+", "-", text).strip("-")


def anchors_for(path: Path) -> set[str]:
    anchors: set[str] = set()
    occurrences: dict[str, int] = {}
    in_fence = False
    fence = ""
    for line in path.read_text(encoding="utf-8").splitlines():
        stripped = line.lstrip()
        if stripped.startswith("```") or stripped.startswith("~~~"):
            marker = stripped[:3]
            if not in_fence:
                in_fence, fence = True, marker
            elif marker == fence:
                in_fence = False
            continue
        if in_fence:
            continue
        heading = HEADING_PATTERN.match(line)
        if heading:
            slug = slugify_heading(heading.group(1))
            if slug:
                index = occurrences.get(slug, 0)
                anchors.add(slug if index == 0 else f"{slug}-{index}")
                occurrences[slug] = index + 1
        anchors.update(match.casefold() for match in HTML_ANCHOR_PATTERN.findall(line))
    return anchors


def iter_links(path: Path) -> Iterable[tuple[int, str]]:
    in_fence = False
    fence = ""
    for line_number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
        stripped = line.lstrip()
        if stripped.startswith("```") or stripped.startswith("~~~"):
            marker = stripped[:3]
            if not in_fence:
                in_fence, fence = True, marker
            elif marker == fence:
                in_fence = False
            continue
        if in_fence:
            continue
        for match in LINK_PATTERN.finditer(line):
            target = match.group("target") or match.group("autolink")
            if target is None:
                target = match.group("bare")[1:-1]
            yield line_number, unescape_target(target)
        for match in PLAIN_URL_PATTERN.finditer(line):
            # Plain URLs are autolinked by GitHub Markdown. Avoid reporting the same URL
            # already captured as an inline-link destination.
            target = match.group(0).rstrip(".,;:!?\"`")
            if target:
                yield line_number, target


def validate_local_links(root: Path, files: list[Path]) -> list[Finding]:
    anchors = {path: anchors_for(path) for path in files}
    findings: list[Finding] = []
    for path in files:
        label = source_label(root, path)
        for line, target in iter_links(path):
            parsed = urlparse(target)
            if parsed.scheme in {"http", "https"} or target.startswith("//"):
                continue
            if parsed.scheme or parsed.netloc:
                findings.append(Finding(label, line, target, "unsupported link scheme"))
                continue
            relative_path, fragment = parsed.path, unquote(parsed.fragment)
            if not relative_path:
                target_path = path
            else:
                target_path = (path.parent / unquote(relative_path)).resolve()
                # GitHub wiki Markdown commonly links to a page by its extensionless title;
                # resolve that conventional form against the checked-in .md file.
                if not target_path.is_file() and not target_path.suffix:
                    markdown_target = target_path.with_suffix(".md")
                    if markdown_target.is_file():
                        target_path = markdown_target
            if not target_path.is_file():
                findings.append(Finding(label, line, target, f"local target does not exist: {source_label(root, target_path) if target_path.is_relative_to(root) else target_path}"))
                continue
            if fragment and fragment.casefold() not in anchors.get(target_path, set()):
                findings.append(Finding(label, line, target, f"anchor does not exist in {source_label(root, target_path)}"))
    return findings


def external_urls(root: Path, files: list[Path]) -> list[tuple[str, int, str]]:
    urls: list[tuple[str, int, str]] = []
    for path in files:
        label = source_label(root, path)
        for line, target in iter_links(path):
            if urlparse(target).scheme in {"http", "https"}:
                urls.append((label, line, target))
    return urls


def fetch_external(url: str, timeout: float, retries: int) -> tuple[bool, str]:
    parsed = urlparse(url)
    # Feedz rejects HEAD and serves a JSON service index, so this reviewed host policy keeps its
    # check explicitly GET-only with an appropriate Accept header. Other hosts are also checked by
    # GET because that is the representation users actually follow from Markdown.
    accept = "application/json" if parsed.hostname in GET_ONLY_HOSTS else "text/html, */*"
    request = Request(url, headers={"Accept": accept, "User-Agent": "groundwork-documentation-verifier/1"}, method="GET")
    last_reason = "unknown error"
    for attempt in range(retries + 1):
        try:
            with urlopen(request, timeout=timeout) as response:
                status = getattr(response, "status", response.getcode())
                if 200 <= status < 400:
                    return True, f"HTTP {status} ({response.geturl()})"
                last_reason = f"HTTP {status}"
                retry = status in RETRYABLE_STATUS_CODES
        except HTTPError as error:
            last_reason = f"HTTP {error.code}"
            retry = error.code in RETRYABLE_STATUS_CODES
        except (TimeoutError, URLError, OSError) as error:
            last_reason = f"{type(error).__name__}: {error}"
            retry = True
        if not retry or attempt == retries:
            break
        time.sleep(min(2 ** attempt, 4))
    return False, last_reason


def validate_external_links(root: Path, files: list[Path], offline: bool, timeout: float, retries: int) -> list[Finding]:
    if offline:
        return []
    findings: list[Finding] = []
    cache: dict[str, tuple[bool, str]] = {}
    for source, line, url in external_urls(root, files):
        if url not in cache:
            cache[url] = fetch_external(url, timeout, retries)
        ok, reason = cache[url]
        if not ok:
            findings.append(Finding(source, line, url, reason))
    return findings


def safe_relative(root: Path, value: object, field: str, findings: list[str]) -> Path | None:
    if not isinstance(value, str) or not value or value.startswith("/"):
        findings.append(f"manifest {field} must be a non-empty repository-relative path")
        return None
    candidate = (root / value).resolve()
    if not candidate.is_relative_to(root):
        findings.append(f"manifest {field} escapes repository root: {value}")
        return None
    return candidate


def validate_manifest(root: Path, manifest_path: Path) -> list[str]:
    findings: list[str] = []
    try:
        document = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        return [f"manifest {manifest_path}: {error}"]
    if not isinstance(document, dict) or document.get("version") != 1 or not isinstance(document.get("snippets"), list):
        return ["manifest must contain version 1 and a snippets array"]
    if not document["snippets"]:
        findings.append("manifest snippets must contain at least one executable source")
    ids: set[str] = set()
    for index, snippet in enumerate(document["snippets"]):
        prefix = f"manifest snippets[{index}]"
        if not isinstance(snippet, dict):
            findings.append(f"{prefix} must be an object")
            continue
        snippet_id = snippet.get("id")
        if not isinstance(snippet_id, str) or not re.fullmatch(r"[a-z0-9]+(?:-[a-z0-9]+)*", snippet_id):
            findings.append(f"{prefix}.id must be a stable lowercase hyphenated identifier")
        elif snippet_id in ids:
            findings.append(f"{prefix}.id is duplicated: {snippet_id}")
        else:
            ids.add(snippet_id)
        for field in ("source", "runner", "workflow"):
            value = safe_relative(root, snippet.get(field), f"{prefix}.{field}", findings)
            if value is not None and not value.is_file():
                findings.append(f"{prefix}.{field} does not exist: {snippet[field]}")
        workflow_job = snippet.get("workflow_job")
        if not isinstance(workflow_job, str) or not re.fullmatch(r"[a-z0-9]+(?:-[a-z0-9]+)*", workflow_job):
            findings.append(f"{prefix}.workflow_job must be a stable lowercase hyphenated job identifier")
        language = snippet.get("language")
        if language not in ALLOWED_LANGUAGES:
            findings.append(f"{prefix}.language is unsupported: {language!r}")
        mode = snippet.get("mode")
        if mode not in ALLOWED_MODES:
            findings.append(f"{prefix}.mode is unsupported: {mode!r}")
        workflow = safe_relative(root, snippet.get("workflow"), f"{prefix}.workflow", findings)
        runner = snippet.get("runner")
        runner_path = safe_relative(root, runner, f"{prefix}.runner", findings)
        if runner_path is not None and runner_path.is_file() and not runner_path.stat().st_mode & 0o111:
            findings.append(f"{prefix}.runner is not executable: {runner}")
        if workflow is not None and workflow.is_file() and isinstance(runner, str):
            workflow_text = workflow.read_text(encoding="utf-8")
            if runner not in workflow_text:
                findings.append(f"{prefix}.runner is not wired into {snippet['workflow']}")
            if not isinstance(workflow_job, str) or f"  {workflow_job}:" not in workflow_text:
                findings.append(f"{prefix}.workflow_job is not present in {snippet['workflow']}")
        if isinstance(snippet.get("source"), str) and isinstance(runner, str):
            runner_text = runner_path.read_text(encoding="utf-8") if runner_path and runner_path.is_file() else ""
            if snippet["source"] not in runner_text:
                findings.append(f"{prefix}.source is not exercised by {runner}")
    return findings


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--manifest", type=Path, default=None)
    parser.add_argument("--offline", action="store_true", help="skip external HTTP checks")
    parser.add_argument("--timeout", type=float, default=8.0)
    parser.add_argument("--retries", type=int, default=2)
    args = parser.parse_args(argv)
    root = args.root.resolve()
    manifest = (args.manifest or root / "docs/v2/executable-snippets.json").resolve()
    files = markdown_files(root)
    findings = validate_local_links(root, files)
    findings.extend(validate_external_links(root, files, args.offline, args.timeout, args.retries))
    findings.extend(Finding("<manifest>", 0, str(manifest.relative_to(root) if manifest.is_relative_to(root) else manifest), reason)
                    for reason in validate_manifest(root, manifest))
    if findings:
        for finding in sorted(findings, key=lambda item: (item.source, item.line, item.target, item.reason)):
            print(finding.format(), file=sys.stderr)
        print(f"Documentation verification failed with {len(findings)} finding(s).", file=sys.stderr)
        return 1
    print(f"Documentation verification passed: {len(files)} Markdown files and executable snippet manifest verified.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
