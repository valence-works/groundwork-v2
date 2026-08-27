# Versioning & Support

## Where packages come from

Previews are published to the public Groundwork Feedz source, **not nuget.org**:

```text
https://f.feedz.io/valence-works/groundwork/nuget/index.json
```

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

## After 1.0

Normal SemVer: breaking → major, compatible features → minor, fixes → patch. Deprecated APIs remain
documented for at least one minor release where practical, and a release note must name the
replacement and the planned removal version **before** removal.

---

## The clean-break rule

Groundwork v2 is a **clean-break pre-1.0 product**. When a preview release note marks a persisted
schema boundary:

> **Discard the earlier preview catalog and create a fresh one from the new declarations.**

There is deliberately **no** in-place migration, compatibility alias, dual-write, or fallback path
between preview catalogs. Plan preview upgrades as recreate-and-reload, not migrate.

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

---

## Provider support matrix

“**Conformance**” = passes the provider-neutral contract suites.
“**Production-supported**” additionally requires a supported deployment topology, operational
guidance, and an owner for provider-specific incidents.

| Component / provider | Status in the first preview | Required topology and evidence |
| --- | --- | --- |
| **SQLite** | Conformance-passing / preview | File-backed or in-memory with the documented connection lifetime; production support requires an operational pilot and runbook |
| **PostgreSQL** | Conformance-passing / preview | PostgreSQL 17-compatible; production support requires an operational pilot and runbook |
| **SQL Server** | Conformance-passing / preview | SQL Server 2022-compatible; production support follows an operational pilot |
| **MongoDB** | Conformance-passing / preview | **Replica-set or sharded** for transactional and exact-append behavior |
| `Groundwork.Testing` | Public provider-author package | Public conformance contracts and deterministic reference provider; **not an application database** |
| `Groundwork.Tool` | Preview | Deployment-time schema planning and explicit authorization only |

**MongoDB standalone deployments are intentionally not represented as production-supported.** They
cannot provide the transaction/session guarantees required by exact append and durable idempotency.

A provider may be marked production-supported in a later release **when the matrix is updated with its
topology, test evidence, and operational owner** — not before.

All relational providers and the reference provider advertise
`groundwork.operational.atomic-commit`. MongoDB advertises it only when the connected deployment
reports transaction support. All five conformance providers support audited, query-only cross-scope
access for scoped units.

---

## What every release must pass

A release is not published until all of it is green:

1. Built from a **tagged commit**.
2. Packs the **explicit public-project allowlist** (23 packages; samples and benchmarks are not
   release artifacts).
3. Emits **SourceLink** and **symbol packages**.
4. Passes the **full four-provider CI suite**.
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
- Prototypes, internal tools, and greenfield services that can recreate their catalog.
- SQLite and PostgreSQL, with a pinned exact version and a fresh catalog per preview boundary.
- Anything where you control the deployment and can act on a release note.

**Wait, or plan carefully**
- Systems that cannot recreate a catalog. There is no preview-to-preview migration path.
- MongoDB standalone for anything using streams — the capabilities are genuinely absent.
- Anything needing a support SLA. Nothing is production-supported yet.

**In every case**
- Pin exact versions; keep the whole Groundwork closure on one version.
- Read release notes before upgrading — persisted schema boundaries are called out explicitly.
- Turn on the analyzer and MSBuild verification so contract changes surface at build, not at runtime.

---

## Links

- [Versioning policy](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/versioning.md)
- [Support matrix](https://github.com/valence-works/groundwork-v2/blob/main/docs/v2/support-matrix.md)
- [Release notes](https://github.com/valence-works/groundwork-v2/tree/main/docs/v2/releases)
- [Issue tracker](https://github.com/valence-works/Groundwork/issues)
