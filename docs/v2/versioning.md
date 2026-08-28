# v2 versioning and breaking-change policy

Groundwork v2 packages use SemVer-compatible pre-1.0 versions. The first
public preview is `0.1.0-preview.1`; a release tag is `v0.1.0-preview.1` and
the `v` prefix is not part of the package version. Preview packages are
published to the public Groundwork Feedz source at
`https://f.feedz.io/valence-works/groundwork/nuget/index.json`.
Published version strings use lowercase prerelease identifiers so artifact and
NuGet cache identities remain identical on every supported platform.

While the major version is zero:

- Preview numbers are immutable. A published package is never replaced.
- A patch increment is used for fixes and additive, source-compatible changes.
- A minor increment is used for a breaking public API, provider behavior, or
  persisted schema contract change. The release notes identify every affected
  package and include migration guidance.
- Provider support status is independent of package version. Conformance is
  evidence of contract behavior, not a production support promise.
- New previews may change before 1.0, but diagnostic codes, public result
  semantics, and storage contracts are changed only with an explicit release
  note and regression proof.

Groundwork v2 is a clean-break pre-1.0 product. When a preview release note
marks a persisted schema boundary, consumers must discard the earlier preview
catalog and create a fresh one from the new declarations. Groundwork does not
ship an in-place migration, compatibility alias, dual-write, or fallback path
between those preview catalogs.

After `1.0.0`, normal SemVer applies: breaking changes require a major version,
compatible features use a minor version, and fixes use a patch version.
Deprecated APIs remain documented for at least one minor release where
practical. A release note must name the replacement and the planned removal
version before removal.

Every release is built from a tagged commit, packs the explicit public-project
allowlist, emits Source Link and symbol packages, and passes the clean-room
package consumer before publication. Package readmes, symbols, Source Link, the
recorded commit, deterministic source paths, and the shipped target frameworks
are asserted against the packed artifacts by `Groundwork.Packaging.Tests`.

Runtime packages multi-target `net8.0` and `net10.0`. Analyzers, source
generators, and the portable contract packages remain `netstandard2.0`.
`Groundwork.SchemaTool.MSBuild` remains `net10.0` because its task loads into
the SDK's own MSBuild process rather than into the consumer's application.

A nuget.org publishing pipeline exists alongside the Feedz preview channel. It
validates on every run and publishes only on an explicit release, behind a
protected environment and a credential that is a maintainer decision to
provision. Publication is accepted only after every
package in that allowlist and `Groundwork.Tool` restore at the exact version
from Feedz.
