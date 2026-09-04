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

## Support tiers

Support status is published in the [provider support matrix](support-matrix.md) using three named
tiers: **Production-supported**, **Compatibility-only**, and **Development/reference-only**. A
production tier applies only to its named provider topology and advertised capabilities. It means
Groundwork maintainers own reproducible package defects and maintain the
[operations runbooks](production-operations.md); it does not transfer database/platform operations
to the maintainers or create a response-time or availability SLA.

Historical preview releases may still name a clean-break catalog boundary.
Those boundaries remain authoritative for the affected preview pair; in
particular, the `0.2.0-preview.1` SQLite reset is not retroactively changed by
the 1.0 policy below.

## Frozen 1.0 contract

The candidate 1.0 contract is recorded as three machine-readable inventories:

- `eng/public-api-v1-net8.0.txt` is the complete exported API on `net8.0`;
- `eng/public-api-v1-net10.0.txt` is the complete exported API on `net10.0`,
  including the `net10.0`-only MSBuild task; and
- `eng/diagnostic-codes-v1.txt` is the complete source-emitted `GW-*` code set.

`Groundwork.Architecture.Tests` derives those inventories from the built
assemblies and product source and compares them byte-for-byte. Set
`GROUNDWORK_UPDATE_CONTRACT_BASELINES=1` only while making an explicitly
reviewed contract change; the changed inventory, release note, compatibility
assessment, and migration guidance must land together. A baseline update is a
review signal, not a way to make an accidental change pass.

The 1.x evolution rules are:

- Patch releases contain source- and binary-compatible fixes. They do not add,
  remove, or rename public members or diagnostic codes.
- Minor releases may add public members and new diagnostic codes. Existing
  signatures, persisted formats, and documented result semantics remain
  compatible.
- An existing diagnostic code keeps its meaning and severity and is never
  reassigned or reused during 1.x. Retired conditions leave a reserved code;
  they do not free it for a different refusal.
- Deprecation starts in a minor release with a documented replacement. The old
  API remains functional throughout 1.x; removal waits for the next major
  version.
- A breaking API, diagnostic-semantic, provider-behavior, or persisted-contract
  change requires the next major version, an explicit release note, and a
  migration path or an explicit statement that no safe migration exists.

## Final preview-to-1.0 transition

The move from the final `0.x` preview to `1.0.0` is the last permitted clean
break in the preview line. It is not automatically a recreate-and-reload
event. Before upgrading a deployed catalog:

1. Back it up and run `groundwork status` with the 1.0 declarations.
2. For an existing Groundwork-shaped catalog with no applicable history, use
   `groundwork adopt` to verify and baseline its physical shape; adoption never
   excuses drift.
3. Apply the reviewed schema plan. Use resumable, idempotent data migration
   steps for value or shape transitions that cannot be expressed as schema DDL
   alone.
4. Recreate and reload only when inspection cannot prove a safe adoption or the
   release note identifies a physical incompatibility for which no authorized
   migration is available.

The 1.0 release note must name the exact final preview, every public or
persisted-contract difference, the applicable adopt/migration sequence, and
the cases that still require recreation. Mixing preview and 1.x packages in
one Groundwork closure is unsupported.

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
validates on every manual run, but a published GitHub release does not trigger
it: previews remain Feedz-only unless a maintainer intentionally dispatches the
workflow. To publish an exact version to nuget.org, dispatch
`.github/workflows/publish-nuget.yml` against the intended release ref with
`publish: true`, and provide that exact version in both `version` and `confirm`:

```bash
gh workflow run publish-nuget.yml --ref v0.4.0-preview.10 \
  -f version=0.4.0-preview.10 -f publish=true -f confirm=0.4.0-preview.10
```

The run remains behind a protected environment and a credential that is a
maintainer decision to provision. Publication is accepted only after every
package in that allowlist and `Groundwork.Tool` restore at the exact version
from nuget.org.
