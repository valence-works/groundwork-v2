# v2 versioning and breaking-change policy

Groundwork v2 packages use SemVer-compatible pre-1.0 versions. The first
public preview is `0.1.0-preview.1`; a release tag is `v0.1.0-preview.1` and
the `v` prefix is not part of the NuGet version.

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

After `1.0.0`, normal SemVer applies: breaking changes require a major version,
compatible features use a minor version, and fixes use a patch version.
Deprecated APIs remain documented for at least one minor release where
practical. A release note must name the replacement and the planned removal
version before removal.

Every release is built from a tagged commit, packs the explicit public-project
allowlist, emits SourceLink and symbol packages, and passes the clean-room
package consumer before publication.
