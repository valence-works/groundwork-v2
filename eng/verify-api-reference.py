#!/usr/bin/env python3
"""Validate exact package XML docs and render a navigable Markdown API reference."""

from __future__ import annotations

import argparse
import hashlib
import html
import json
import re
import sys
import zipfile
from pathlib import Path
from xml.etree import ElementTree


VERSION = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9a-z.-]+)?$")
BASELINE = "eng/api-documentation-baseline.json"


def package_inventory(root: Path) -> list[tuple[str, Path]]:
    rows: list[tuple[str, Path]] = []
    seen: set[str] = set()
    for line_number, raw in enumerate((root / "eng/public-packages.txt").read_text().splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        parts = [part.strip() for part in raw.split("|")]
        if len(parts) != 2 or not all(parts):
            raise ValueError(f"invalid public package entry at line {line_number}: {raw!r}")
        package_id, project = parts
        key = package_id.casefold()
        if key in seen:
            raise ValueError(f"duplicate public package id at line {line_number}: {package_id}")
        seen.add(key)
        rows.append((package_id, root / project))
    if not rows:
        raise ValueError("public-packages.txt has no package entries")
    return rows


def assembly_name(project: Path) -> str:
    document = ElementTree.parse(project)
    return next(
        (element.text.strip() for element in document.getroot().iter("AssemblyName") if element.text),
        project.stem,
    )


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def public_api_snapshots(root: Path) -> tuple[dict[str, dict[str, list[str]]], dict[str, str]]:
    rows: dict[str, dict[str, list[str]]] = {}
    hashes: dict[str, str] = {}
    for framework in ("net8.0", "net10.0"):
        path = root / f"eng/public-api-v1-{framework}.txt"
        content = path.read_bytes()
        hashes[framework] = digest(content)
        assembly_rows: dict[str, list[str]] = {}
        for line_number, line in enumerate(content.decode().splitlines(), 1):
            if ": " not in line:
                raise ValueError(f"invalid public API snapshot row in {path} at line {line_number}")
            assembly = line.split(": ", 1)[0]
            assembly_rows.setdefault(assembly, []).append(line)
        rows[framework] = assembly_rows
    return rows, hashes


def text_content(element: ElementTree.Element | None) -> str:
    if element is None:
        return ""
    return " ".join("".join(element.itertext()).split())


def documented_public_members(
    members: list[dict[str, str]], public_rows: list[str]
) -> tuple[int, list[dict[str, str]]]:
    matched: set[int] = set()
    visible: list[dict[str, str]] = []
    for member in members:
        identifier = member["id"]
        if len(identifier) < 3 or identifier[1] != ":":
            continue
        body = identifier[2:].split("(", 1)[0].replace("#ctor", "ctor").replace("#cctor", "cctor")
        body = re.sub(r"``\d+$", "", body)
        for index, row in enumerate(public_rows):
            if body in row:
                matched.add(index)
                visible.append(member)
                break
    return len(matched), visible


def inspect_package(
    package_id: str,
    project: Path,
    package_path: Path,
    version: str,
    api_rows: dict[str, dict[str, list[str]]],
) -> list[dict[str, object]]:
    expected_name = f"{package_id}.{version}.nupkg"
    if package_path.name != expected_name:
        raise ValueError(f"package filename does not match exact version: {package_path.name}")
    primary_name = assembly_name(project)
    primary = primary_name + ".dll"
    with zipfile.ZipFile(package_path) as archive:
        entries = set(archive.namelist())
        nuspecs = sorted(entry for entry in entries if entry.lower().endswith(".nuspec"))
        if len(nuspecs) != 1:
            raise ValueError(f"{package_id}: expected exactly one nuspec, found {len(nuspecs)}")
        metadata = ElementTree.fromstring(archive.read(nuspecs[0]))
        versions = [element.text for element in metadata.iter() if element.tag.rsplit("}", 1)[-1] == "version"]
        if version not in versions:
            raise ValueError(f"{package_id}: nuspec does not record exact version {version}")
        assemblies = sorted(
            entry for entry in entries
            if entry.endswith("/" + primary)
            and (entry.startswith("lib/") or entry.startswith("tools/") or entry.startswith("analyzers/"))
        )
        if not assemblies:
            raise ValueError(f"{package_id}: no shipped primary assembly {primary}")
        rows: list[dict[str, object]] = []
        for assembly in assemblies:
            xml_path = assembly[:-4] + ".xml"
            if xml_path not in entries:
                raise ValueError(f"{package_id}: {assembly} has no adjacent XML documentation file {xml_path}")
            xml_bytes = archive.read(xml_path)
            try:
                xml_root = ElementTree.fromstring(xml_bytes)
            except ElementTree.ParseError as error:
                raise ValueError(f"{package_id}: malformed XML documentation file {xml_path}: {error}") from error
            members = []
            for member in xml_root.findall("./members/member"):
                identifier = member.get("name", "").strip()
                if not identifier:
                    raise ValueError(f"{package_id}: {xml_path} contains a member without an identifier")
                members.append({"id": identifier, "summary": text_content(member.find("summary"))})
            framework = "net10.0" if "/net10.0/" in assembly else "net8.0"
            public_rows = api_rows[framework].get(primary_name, [])
            documented_count, documented_members = documented_public_members(members, public_rows)
            rows.append({
                "assembly": assembly,
                "assembly_sha256": digest(archive.read(assembly)),
                "xml": xml_path,
                "xml_sha256": digest(xml_bytes),
                "members": sorted(members, key=lambda item: str(item["id"])),
                "documented_members": sorted(documented_members, key=lambda item: str(item["id"])),
                "public_api_framework": framework,
                "public_api_members": len(public_rows),
                "documented_public_members": documented_count,
                "undocumented_members": len(public_rows) - documented_count,
            })
    return rows


def baseline_key(package_id: str, row: dict[str, object]) -> str:
    return f"{package_id}|{row['assembly']}"


def baseline_document(
    rows: list[tuple[str, list[dict[str, object]]]], snapshot_hashes: dict[str, str]
) -> dict[str, object]:
    return {
        "schemaVersion": 1,
        "publicApiSnapshots": snapshot_hashes,
        "entries": {
            baseline_key(package_id, row): {
                "publicApiMembers": row["public_api_members"],
                "maximumUndocumentedMembers": row["undocumented_members"],
            }
            for package_id, assemblies in rows
            for row in assemblies
        },
    }


def validate_baseline(
    path: Path,
    rows: list[tuple[str, list[dict[str, object]]]],
    snapshot_hashes: dict[str, str],
) -> None:
    baseline = json.loads(path.read_text())
    expected_keys = {baseline_key(package_id, row) for package_id, assemblies in rows for row in assemblies}
    entries = baseline.get("entries")
    if baseline.get("schemaVersion") != 1 or baseline.get("publicApiSnapshots") != snapshot_hashes or not isinstance(entries, dict):
        raise ValueError(f"{path}: baseline schema or public API snapshot hashes do not match")
    if set(entries) != expected_keys:
        raise ValueError(f"{path}: baseline assembly keys do not match the exact package inventory")
    for package_id, assemblies in rows:
        for row in assemblies:
            key = baseline_key(package_id, row)
            entry = entries[key]
            if entry.get("publicApiMembers") != row["public_api_members"]:
                raise ValueError(f"{key}: public API count changed; review and update the documentation baseline")
            if row["undocumented_members"] > entry.get("maximumUndocumentedMembers", -1):
                raise ValueError(
                    f"{key}: undocumented public surface increased from the reviewed maximum "
                    f"{entry.get('maximumUndocumentedMembers')} to {row['undocumented_members']}"
                )


def safe_name(package_id: str, assembly_path: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", f"{package_id}-{assembly_path}".lower()).strip("-") + ".md"


def render_page(version: str, package_id: str, row: dict[str, object]) -> str:
    members = row["documented_members"]
    lines = [
        f"# {package_id} API reference",
        "",
        f"Exact package version: `{version}`  ",
        f"Assembly: `{row['assembly']}`  ",
        f"Assembly SHA-256: `{row['assembly_sha256']}`  ",
        f"XML documentation: `{row['xml']}`  ",
        f"XML SHA-256: `{row['xml_sha256']}`  ",
        f"Public API snapshot: `{row['public_api_framework']}` ({row['public_api_members']} members)  ",
        f"Documented public members: {row['documented_public_members']}  ",
        f"Reviewed maximum undocumented members: {row['undocumented_members']}",
        "",
        "| Kind | Member | Summary |",
        "| --- | --- | --- |",
    ]
    for member in members:
        identifier = str(member["id"])
        kind = {"T": "Type", "M": "Method", "P": "Property", "F": "Field", "E": "Event"}.get(identifier[:1], "Member")
        summary = html.escape(str(member["summary"]), quote=False).replace("|", "&#124;") or "_No summary text._"
        escaped_identifier = html.escape(identifier, quote=False).replace("|", "&#124;")
        lines.append(f"| {kind} | <code>{escaped_identifier}</code> | {summary} |")
    return "\n".join(lines) + "\n"


def render_reference(output: Path, version: str, rows: list[tuple[str, list[dict[str, object]]]]) -> None:
    output.mkdir(parents=True, exist_ok=True)
    lines = [
        "# Groundwork v2 exact-package API reference",
        "",
        f"Validated package version: `{version}`",
        "",
        "Generated from the exact shipped nupkg assemblies and their adjacent XML documentation.",
        "The public API counts are bound to the reviewed assembly snapshots; documentation coverage may not regress beyond the checked-in baseline.",
        "",
        "| Package | Assembly | Public API | Documented | Undocumented baseline | Reference |",
        "| --- | --- | ---: | ---: | ---: | --- |",
    ]
    for package_id, assemblies in rows:
        for row in assemblies:
            filename = safe_name(package_id, str(row["assembly"]))
            (output / filename).write_text(render_page(version, package_id, row))
            lines.append(
                f"| `{package_id}` | `{row['assembly']}` | {row['public_api_members']} | "
                f"{row['documented_public_members']} | {row['undocumented_members']} | [{filename}]({filename}) |"
            )
    (output / "index.md").write_text("\n".join(lines) + "\n")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("packages", type=Path, help="directory containing exact versioned nupkg files")
    parser.add_argument("version", help="exact package version")
    parser.add_argument("output", type=Path, help="output directory for the navigable Markdown reference")
    parser.add_argument("--write-baseline", action="store_true", help="write the reviewed baseline from these exact artifacts")
    args = parser.parse_args()
    if not VERSION.fullmatch(args.version):
        parser.error(f"invalid package version: {args.version}")
    root = Path(__file__).resolve().parent.parent
    if not args.packages.is_dir():
        raise ValueError(f"package directory does not exist: {args.packages}")
    api_rows, snapshot_hashes = public_api_snapshots(root)
    rows = []
    for package_id, project in package_inventory(root):
        package_path = args.packages / f"{package_id}.{args.version}.nupkg"
        if not package_path.is_file() or package_path.is_symlink():
            raise ValueError(f"missing exact package artifact: {package_path}")
        rows.append((package_id, inspect_package(package_id, project, package_path, args.version, api_rows)))
    baseline_path = root / BASELINE
    if args.write_baseline:
        baseline_path.write_text(json.dumps(baseline_document(rows, snapshot_hashes), indent=2, sort_keys=True) + "\n")
    validate_baseline(baseline_path, rows, snapshot_hashes)
    render_reference(args.output, args.version, rows)
    print(f"Validated XML documentation and its reviewed coverage baseline for {len(rows)} public packages.")
    print(f"Wrote navigable reference to {args.output / 'index.md'}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, json.JSONDecodeError, zipfile.BadZipFile) as error:
        print(f"API reference verification failed: {error}", file=sys.stderr)
        raise SystemExit(1)
