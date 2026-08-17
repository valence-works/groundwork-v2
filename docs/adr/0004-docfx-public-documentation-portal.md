# ADR 0004: Build the public documentation portal with DocFX

- Status: accepted
- Date: 2026-08-17

## Context

Groundwork needs one public, searchable, versioned documentation surface for
conceptual guides, provider matrices, compiled samples, and the complete .NET
API. Markdown files scattered through the repository are useful source material
but do not provide navigation, search, a stable version URL, or generated API
pages.

## Decision

Use DocFX 2.78.5 from a repository-local .NET tool manifest. Build conceptual
Markdown and the explicit public-package project allowlist into one static site
with the modern template and local search index. Put the current preview under
`/v0.1/`; later minor lines receive separate routes instead of overwriting it.
Publish the generated static artifact to GitHub Pages, while keeping the local
build as the authoritative release gate.

The quickstart is compiled and executed from `samples/` and included in the
portal from that source file, preventing a second uncompiled copy.

This follows DocFX's documented support for [.NET API generation](https://dotnet.github.io/docfx/docs/dotnet-api-docs.html),
the [modern searchable template](https://dotnet.github.io/docfx/docs/template.html),
and [static-site publication](https://dotnet.github.io/docfx/).

## Consequences

- One tool produces conceptual and API documentation and an offline search
  index.
- Site publication is hosting-only; a GitHub outage cannot prevent local docs
  validation or package release evidence.
- DocFX does not own Groundwork's release-version lifecycle. The repository
  must add a new version route and retain prior routes when the documented minor
  line changes.
- The explicit API project list intentionally duplicates the release allowlist;
  an acceptance test compares them so a new public package cannot ship without
  reference documentation.
