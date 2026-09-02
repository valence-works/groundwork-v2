# Versioning & Support

## Where packages come from

Previews are published to the public Groundwork Feedz source, **not nuget.org**:

```text
https://f.feedz.io/valence-works/groundwork/nuget/index.json
```

Feedz stays the preview channel. A nuget.org release pipeline is in place and validated on every
manual run, but a published GitHub release does not start it. An intentional dispatch must provide
the exact version in both `version` and `confirm`, runs behind a protected environment with required
reviewers, and needs a credential the repository does not hold. Publishing to a public feed is a
maintainer decision, not something CI arrives at. See [Installation](Installation) for the exact
dispatch command.

## Target frameworks

Runtime packages multi-target `net8.0` and `net10.0`; analyzers, source generators, and the portable
contract packages stay `netstandard2.0`. The two runtime targets are one implementation compiled
twice, not two variants — nothing is compiled conditionally per target, and the fingerprints and
canonical documents that persist across processes are pinned to literals by suites that run on each
target. `Groundwork.SchemaTool.MSBuild` is `net10.0` only, because an MSBuild task loads into the
SDK's MSBuild process rather than into the application; see
[Installation](Installation) for what that does and does not constrain.

## Version format

- Packages use SemVer-compatible **pre-1.0** versions: `0.1.0-preview.1`, `0.2.0-preview.1`, …
- A release **tag** is `v0.2.0-preview.1`; the `v` prefix is **not** part of the package version.
- Prerelease identifiers are **lowercase**, so artifact and NuGet cache identities are identical on
  every supported platform.

## While the major version is zero

| Rule | Detail |
| --- | --- |
| **Preview numbers are immutable** | A published package is **never** replaced. Pin exact versions. |
| **Patch** | Fixes and additive, source-compatible changes |
| **Minor** | A breaking public API, provider behavior, or persisted schema contract change. Release notes name **every** affected package and include migration guidance. |
| **Support status is independent of version** | Conformance is evidence of contract behavior, **not** a production support promise. |
| **Stable surfaces** | Diagnostic codes, public result semantics, and storage contracts change only with an explicit release note and regression proof. |

## Frozen 1.0 contract and 1.x evolution

The candidate 1.0 API and diagnostic set is not an informal promise. It is recorded in exhaustive,
machine-checked manifests for `net8.0`, `net10.0`, and every source-emitted `GW-*` code. The
[canonical versioning policy](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/versioning.md)
names those files and the controlled update procedure.

During 1.x, patches are source- and binary-compatible fixes. Minors may add APIs and diagnostic
codes, but they do not change an existing signature, persisted contract, result meaning, or code
meaning. A diagnostic code is never reassigned. Deprecation names a replacement in a minor release;
the deprecated API remains functional for the rest of 1.x and removal waits for the next major.

---

## Final preview-to-1.0 transition

The move from the final preview to `1.0.0` is the last permitted preview-line clean break, but it is
not automatically a catalog reset. Back up the deployment, inspect it with the 1.0 declarations,
use `groundwork adopt` to verify and baseline a Groundwork-shaped catalog when history is absent,
and apply authorized schema and resumable data migrations. Recreate only when the 1.0 release note
identifies a physical incompatibility for which inspection cannot prove a safe migration.

Historical preview boundaries remain authoritative for the exact releases they name. The reset
below is therefore still required when moving across that boundary.

### `0.2.0-preview.1` — SQLite catalog reset (required)

`Groundwork.Sqlite` now stores portable ordinal strings with the registered `GROUNDWORK_UTF16_ORDINAL`
collation, and ordinary indexes inherit it. Equality/range predicates and index ordering therefore use
the required .NET UTF-16 ordinal semantics — including supplementary characters — and native plans can
use the declared indexes.

**Catalogs created by an earlier v2 preview physically declare those columns as `BINARY` and are not
compatible.** Delete and recreate every such catalog, then apply the current declarations before
writing data.

PostgreSQL, SQL Server, and MongoDB do **not** need a physical catalog reset for this correction.
All applications should still move their complete Groundwork closure to the exact release version
rather than mixing previews.

Other changes in `0.2.0-preview.1`:

- `Groundwork.Store` exposes `StorageUnit.CreateQueryRenderOptions`, so consumers can derive query
  index metadata from the admitted declaration. Optional index selection remains provider-default
  evidence and **never** becomes an optimizer hint.
- Relational query results report an expected provider-default index separately from whether a native
  hint was applied.
- MongoDB transactional same-identity races return portable deterministic write outcomes;
  wrapper-owned transactions retry transient write-conflict bodies.

### `0.4.0-preview.1` — all-provider catalog reset (required)

This release changes the canonical schema document and subject fingerprints across the complete
provider family. **Discard every catalog created by an earlier preview and create a fresh one from
the current declarations.** Groundwork provides no in-place migration, compatibility alias,
dual-write, or fallback path across this boundary. Re-run `groundwork schema emit`, rebuild any
assembly carrying a generated `GroundworkSchema` attribute, and apply the current declaration to
the fresh catalog before serving traffic.

The runtime reports an earlier-preview catalog as `GW-SCHEMA-006`, naming the affected storage unit
and the discard remedy. See the complete [0.4.0-preview.1 release notes](../v2/releases/0.4.0-preview.1.md)
for the public API, provider behavior, schema authorization, hosting, and package-closure changes.

### `0.4.0-preview.2` — MongoDB exact-batch atomicity fix

This patch preview preserves the `0.4.0-preview.1` public API and persisted catalog contract. Existing
`0.4.0-preview.1` catalogs remain compatible; no discard, migration, or schema re-application is
required for this update.

MongoDB exact batches executed inside an explicit unit of work now stop at the first modeled
create-only or compare-and-swap conflict, abort the transaction, poison that unit of work, and throw
the provider-neutral `BatchWriteException`. Wrapper-owned exact-batch transactions likewise never
commit a losing operation or a trailing write. See the complete
[0.4.0-preview.2 release notes](../v2/releases/0.4.0-preview.2.md).

### `0.4.0-preview.3` — MongoDB portable JSON admission

This patch preview preserves the `0.4.0-preview.2` public API and persisted catalog contract.
Existing `0.4.0-preview.2` catalogs remain compatible; no discard, migration, or schema
re-application is required for this update.

MongoDB schema and runtime admission now accept the native BSON representations produced by
portable JSON, including objects, arrays, strings, numbers, booleans, and JSON literal `null`.
Required JSON columns continue to reject missing values and CLR `null`, while JSON literal `null`
remains valid content. Mongo-only BSON values are refused recursively at the codec boundary, so
writes and later admission use the same accepted-type vocabulary. See the complete
[0.4.0-preview.3 release notes](../v2/releases/0.4.0-preview.3.md).

### `0.4.0-preview.4` — Index-compatible relational ordering

This patch preview preserves the `0.4.0-preview.3` public API and persisted catalog contract.
Existing `0.4.0-preview.3` catalogs remain compatible; no discard, migration, or schema
re-application is required for this update.

Relational renderers now omit redundant null-rank expressions only when the selected
provider-resolved index declaration proves an ordered column is non-nullable. Matching composite
indexes can therefore satisfy the requested order without an unnecessary sort, while nullable or
unproven columns retain portable explicit null ordering. Provider-specific string, decimal, GUID,
and keyset-continuation semantics remain unchanged. See the complete
[0.4.0-preview.4 release notes](../v2/releases/0.4.0-preview.4.md).

### `0.4.0-preview.5` — Retention evidence, deployment safety, and array search

This preview adds bounded affected-key evidence for exact retention, refuses
hosted schema auto-apply outside Development, makes the first-party schema tool
provider-ready in an isolated deployment, and adds native provider-neutral
substring matching over string-array elements.

Existing preview.4 catalogs remain compatible. A schema identity changes only
when a JSON column opts into `ElementSearchKey`; review and apply that explicit
plan before using Unicode ordinal ignore-case element-substring queries. No
catalog discard is required. See the complete
[0.4.0-preview.5 release notes](../v2/releases/0.4.0-preview.5.md).

### `0.4.0-preview.6` — Exact-package documentation evidence

This patch preview ships XML documentation with every public assembly and
adds the exact-package API-reference, package README, executable snippet,
documentation-link, generated-matrix, all-provider sample, published-portal,
and newcomer evidence gates.

There is no public runtime API, diagnostic, provider-behavior, or persisted
schema change from preview.5. Existing catalogs remain compatible and require
no schema apply or recreation. See the complete
[0.4.0-preview.6 release notes](../v2/releases/0.4.0-preview.6.md).

---

## Provider support matrix

Conformance is implementation evidence, not a tier. **Production-supported** names an exact topology
with maintained runbooks and best-effort maintainer ownership of reproducible Groundwork defects.
**Compatibility-only** is capability-gated without a production suitability promise.
**Development/reference-only** is non-production. No tier creates a response-time or availability
SLA.

The exact provider, version, and topology assignments are maintained only in the canonical
**[v2 provider support matrix](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/support-matrix.md)**.
The canonical table separates live-evidenced production topologies from compatible deployments that
have not passed their own live conformance, schema-tool, and concurrency lanes.

The deployment owner owns the database/platform, capacity, credentials, backup/restore, upgrades,
and failover. Groundwork maintainers own reproducible package defects within the named boundary. See
the canonical
**[production operations runbooks](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/production-operations.md)**
for the runbooks and incident evidence contract.

All relational providers and the reference provider advertise
`groundwork.operational.atomic-commit`. MongoDB advertises it only when the connected deployment
reports transaction support. All shipped providers support audited, query-only cross-scope access
for scoped units when the connected deployment advertises the required capability.

---

## What every release must pass

A release is not published until all of it is green:

1. Built from a **tagged commit**.
2. Packs the **explicit public-project allowlist** (26 packages; samples and benchmarks are not
   release artifacts).
3. Emits **Source Link** and **symbol packages**, asserted against the packed artifacts rather
   than against the project settings meant to produce them.
4. Passes the **full provider CI suite**.
5. Passes **package layout verification**.
6. Passes the **clean-room package-only public API consumer** — built outside the repository source
   graph, from packed artifacts, with no project references, internal access, reflection, or friend
   assemblies, built **twice** and run after each build.
7. After publication: a **clean restore of every package and the tool at the exact version** from
   Feedz.

Publication is accepted only after every package in the allowlist **and** `Groundwork.Tool` restore at
the exact version.

---

## Adoption guidance

**Reasonable now**
- Production services on a production-supported topology that can follow the operational runbook.
- Prototypes, internal tools, and greenfield services that can follow an authorized migration plan.
- Existing Groundwork-shaped catalogs that can pass inspection and, when needed, `groundwork adopt`.
- Anything where you control the deployment and can act on a release note.

**Wait, or plan carefully**
- Systems that cannot take a verified backup or tolerate the transition procedure named by the
  release note.
- MongoDB standalone for anything using streams — the capabilities are genuinely absent.
- Anything needing a contracted response-time or availability SLA; support here is best effort.

**In every case**
- Pin exact versions; keep the whole Groundwork closure on one version.
- Read release notes before upgrading — persisted schema boundaries are called out explicitly.
- Turn on the analyzer and MSBuild verification so contract changes surface at build, not at runtime.

---

## Links

- [Versioning policy](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/versioning.md)
- [Support matrix](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/support-matrix.md)
- [Production operations](Production-Operations)
- [Release notes](https://github.com/valence-works/groundwork-v2/tree/main/docs/v2/releases)
- [Issue tracker](https://github.com/valence-works/Groundwork/issues)
