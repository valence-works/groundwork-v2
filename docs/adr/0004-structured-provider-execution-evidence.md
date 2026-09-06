# ADR 0004: Publish provider-owned structured execution evidence

- Status: Proposed; contract and producer-to-consumer proof under review
- Date: 2026-09-05
- Work: #405
- Builds on: [0001](0001-kernel-contract-family-charter.md),
  [0002](0002-physical-storage-is-the-kernel.md),
  [0003](0003-audited-privileged-cross-scope-queries.md)

## Context

`IProviderCommandObserver` counts session commands and exposes diagnostic command
text. Consumers that parse that text to verify scope, ordering or boundedness
must duplicate provider grammar. A renderer can change its correct spelling
without changing semantics, breaking those consumers. Conversely, merely
copying a requested query into an evidence object would describe intent, not
what the provider actually rendered and executed.

The existing opt-in explain assertion inspects native plans, but only answers
whether a selected index was chosen. It does not expose a reusable description
of the winning access path. It also obtains a separate plan: SQLite uses
`EXPLAIN QUERY PLAN`; the other providers may execute a diagnostic replay. Such
a plan must not be described as telemetry captured from the original read.

## Proposed decision

Groundwork owns a value-free execution-evidence contract. It reports facts about
provider execution, never application routes, expected workload cardinalities,
latency budgets, acceptance decisions or domain-specific exceptions. Consumers
own those policies and compare them with the reported facts.

The immutable contract belongs beside the provider command observer in
`Groundwork.Kernel`, with BCL-only data types and no provider, Store, runtime,
SQL, BSON, XML or I/O dependencies. Provider-native interpretation stays in the
adapters; shared relational emission belongs in the relational substrate.
`Groundwork.Diagnostics` continues to provide generic diagnostic plumbing, not
an application contract family.

### Producer provenance

1. Rendering produces the native command and its semantic shape together.
   Predicate, ordering, projection, scope and paging emission pair native
   fragments and evidence facts in the same emission branches. A render-scoped
   collector returns one immutable command/shape pair; there is no second traversal that
   independently predicts the finished command from the original request.
   Rewritten search keys, provider scope predicates, continuation guards and
   lookahead limits describe the effective physical emission.
2. The session issues the same rendered command. An opaque invocation identity
   binds immutable shape, command ordinal and completion outcome. Render-only
   output is not execution evidence. Failures and cancellation cannot produce
   a successful-execution claim; errors contain stable categories, not raw
   exception messages or provider payloads.
3. Explicitly requested plan collection uses that command and its actual bound
   parameters. Each native statement has its own correlated plan observation;
   a batch is not proved by finding one favorable statement in a combined plan.
4. Plan provenance distinguishes an estimated explain, an executed diagnostic
   replay and telemetry from the original execution. Only the last may claim
   original-execution statistics. A replay is opt-in and read-only; no write is
   repeated for evidence. Collection must preserve transaction/session gates
   and restore provider diagnostic settings even on failure.

The old `ProviderCommandEvent` constructor, observer callback and round-trip
count behavior remain compatible. Structured evidence is an additive optional
capability, not an extra old command event per explain statement. Its explicit
plan-collection accounting documents additional diagnostic work. With no
evidence request, ordinary execution does not acquire an explain or incur that
extra provider work.

The initial API direction is a separate `IProviderExecutionObserver` capability
implemented by an object attached through the existing session command-observer
option. It opts into shape collection and, separately, native-plan collection.
Its `ObserveExecution(ProviderExecutionEvidence evidence)` callback receives a
terminal immutable observation for each supported issued command, not a
render-time promise. The observation identifies a capture, invocation and
command ordinal. Statement-level shapes/plans have ordinals within that command.
The concrete public signatures are accepted only after the first executable
consumer proof; this ADR does not represent an already available API.

An actual-command outcome and a plan-collection outcome are independent. A
successful read followed by a failed explain remains a successful read with
failed plan collection, even if an enabled explain assertion then throws.
This separation describes the evidence, not a promise that the public query
returns normally after diagnostic failure. When requested plan collection
throws, the provider emits the terminal observation and propagates that original
exception. A deferred explain-assertion failure is treated the same way. A
terminal observer exception must not mask either pending diagnostic failure;
without a pending failure, an observer exception still propagates. An unsupported
native-plan grammar is unavailable evidence, not an exception by itself. This
policy applies across providers, including MongoDB, so enabling the same
capability does not silently change error handling by provider.
Try/finally emission preserves failed/cancelled actual-command outcomes without
converting either to success. The existing observer remains pre-execution;
diagnostic explain round trips, historically excluded from `RoundTrips`, are
accounted for only on the new evidence surface. No provider write is replayed.

Mapped relational sessions assign one fresh capture context shared by their point-read
and bounded-query producers; each public read gets fresh invocation, command,
statement and value-binding identities. It does not share these identities
across sessions. Command ordinals are within an invocation, and statement
ordinals are within a command; they are not global capture counters. Each
single-command point-read invocation therefore has command/statement ordinal
zero. Write probes do not
pretend to be independent public-read invocations: their enclosing-operation
correlation is still unmapped in this first proof. Point reads and queries on
the same target in one session share their opaque target identity, while their
invocation, command and statement identities remain distinct.

PostgreSQL and SQL Server opt their point-read equality emitters into the same
value-free key-binding helper used by SQLite. The helper records a binding only
when explicitly called by the native emitter; default adapter implementations
remain unsupported. Native equality and lock clauses are unchanged. PostgreSQL
can report an emitted `FOR UPDATE`, while SQL Server and SQLite report no such
clause. Their native limit remains absent and enforced uniqueness remains
unobserved. PostgreSQL also maps its actual bounded equality/range, ordinal-key
ordering, null ranks and shared native paging emission. GUID text-cast ordering
remains unsupported until its transform is represented. SQL Server maps its
own equality/range and ordering branches, including the ordinal string-length
companion, null ranks and persisted ordinal-identity transforms. Its native
offset is explicitly zero for limit-only pages because that is what SQL Server
emits. Merely sharing a capture context does not opt a renderer in; unsupported
forms still withhold the entire semantic shape.

The terminal read callback runs after the reader has been closed, including on
native failure. Observer exceptions after a successful read propagate as
observer failures, while an observer exception during an already failed or
cancelled read cannot replace the original provider/cancellation exception.
Pre-issue validation or cancellation produces no issued-command observation.
For relational queries, an execution marker is raised immediately before the
driver reader call, after materializer validation, cancellation preflight and
command creation. The old pre-execution observer event is not that marker:
it retains its earlier timing and may be followed by a preflight refusal.
Callback options and terminal observation retain SQLite's existing observer
guard; the capability survives its observer wrapper. PostgreSQL and SQL Server
use an owner-local structured-callback guard before waiting on their
non-reentrant session gates. A structured callback cannot enter or dispose a
session or provider belonging to that same owner. Independent owners remain
usable, and callback depth is restored even when the observer throws. Only the
new options getter and terminal callback use this guard: the raw observer and
its legacy command/registration interfaces retain their existing behavior.
The unit-of-work preflight boundary must also refuse callback-driven commit,
rollback, staging and disposal before flushing or changing transaction state;
guarding only session calls after a commit would be too late.
SQLite's existing asynchronous point-read API checks cancellation before
entering its synchronous execution path. This evidence slice does not add
mid-command SQLite cancellation support or claim that a later token change was
observed. The generic terminal cancellation outcome is used only when the
actual execution surface raises cancellation, not merely because a token was
cancelled after an otherwise successful command.

### Contract facts and uncertainty

The first executable slice is one SQLite single-source bounded page (without
count, join, reduction or cross-scope access) plus a relational point read.
MongoDB adds the corresponding aggregate/find and point-read redaction proof.
This deliberately limits the first proof, not #405's final four-provider scope.
Count batches, transactional Mongo split commands, joins and cross-scope access
must remain explicitly unsupported until their command/statement/target
correlation is implemented; they may not borrow a supported single-page shape.
Other operation kinds remain identifiable but must report unsupported
semantic/plan coverage until mapped.
Unsupported shapes must not fall back to command-text parsing or a successful
empty evidence object.

The contract separates:

- Command identity and outcome: provider, operation kind, probe/read/write role,
  invocation and statement identity, executed/succeeded/cancelled/failed state.
- Physical target binding: declared unit/column identities and opaque physical
  target identity, including whether scope is unscoped, bound by a predicate,
  bound by a physical target, or explicitly privileged across scopes.
- Rendered shape: Boolean predicate structure, referenced columns, comparison
  semantics, value-free binding identities, projection, effective ordering and
  null placement, provider ordering transforms, offset, continuation presence,
  and native fetch limit. A flattened list cannot stand in for AND/OR/NOT.
- Native plan facts: winning tree, access kind, resolved index identity and
  key shape where available, explicit sort and its ordering, limit, observed
  rows and spill facts. Logical index selection is separate from physical
  optimizer choice; rejected candidate plans never count as the winner.
- Availability: not requested, unsupported, collected or failed, independently
  for shape and plan coverage. Missing fields are unknown, not false, zero,
  no-spill, no-sort, or an accepted plan. Collection is not a product verdict.

For the initial slice, predicate evidence is a closed set of complete
conjunction facts (comparison column/operator/semantics, scope and continuation
roles), emitted in the same branches as the native predicates. An unsupported
Boolean shape makes the whole predicate proof unsupported; a partial list or
fingerprint cannot silently attest to AND/OR/NOT semantics it did not represent.
This avoids introducing a general-purpose query AST or parser solely for the
first proof. Broader supported shapes must acquire an explicit complete
representation before a consumer can admit them.

The SQLite, PostgreSQL and SQL Server page producers support non-null equality/range
conjunctions and ordinary columns, including exact persisted ordinal-identity
keys. A unique physical-to-logical mapping identifies the emitted source column;
conflicting mappings withhold the entire shape regardless of enumeration order.
Ordering on a distinct mapped physical column records its physical-search-key
transform, including when the shared order emitter is used. Other transformed
search keys, element mappings, continuation pages and unmapped forms withhold
the whole shape.
String comparison and ordering report the emitted ordinal collation. An absent
null-placement value means that no explicit null-rank expression was emitted;
it is not a catalog witness of column nullability or uniqueness.

The chosen-index boolean is independent from a completely mapped winning-plan
operator structure. `ProviderPlanForest` preserves all native nodes, their
parent relationships and any sibling roots, without inventing execution
dependencies. Its completeness claim covers operator structure only, not sort
keys, spill, row counts, or enforced key shape. A null forest means incomplete
or unmapped structure, never an empty plan or proof of no sort. Typed node guards
reject unknown operators, invalid relationships and contradictory fields.

A standalone native row-limiting operator is preserved as `ProviderPlanOperator.Limit`,
including its parent and children. This structural fact carries no numeric
bound: an estimated row count must not be substituted for the bound emitted by
the renderer. It also cannot carry access, index or sort attributes.

A fused native top-N sort is preserved as one `ProviderPlanOperator.TopNSort`
node. It retains both sorting and row-limiting semantics without inventing a
second native node or changing parentage. Consumers checking for either kind
of work must also consider this fused operator. It carries no numeric bound,
spill claim or access identity; a sort purpose is optional and must be observed.

SQLite maps the actual four-column `EXPLAIN QUERY PLAN` response for a single
physical source. It distinguishes table scans, index scans/searches, integer
primary-key searches and explicit ordering/grouping/distinct sorts. The native
target must match exactly, and logical index identity is resolved only from an
exact selected physical index name. Unknown indexes, operators or malformed
graphs withhold the entire forest; independently observed index-choice facts
may still be reported. Native IDs are normalized locally, preserving SQLite's
root sentinel and every row. This parser is provider-owned and closed against
unrecognized output grammar; it does not interpret the opaque search-constraint
body as predicate proof. Queries without a requested index may still expose
their actual access path, without fabricating a successful index-choice fact.

Spill, row-count and enforced key-shape facts remain unknown until a provider
actually collects them. They are required for consumers that need those facts,
and four-provider acceptance cannot be claimed by populating them from
declarations, estimated counts or requested query metadata.

PostgreSQL resolves the quoted query relation through `to_regclass` on the same
connection and transaction used for the estimated explain. The resulting
namespace must match each access node's verbose `Schema` and `Relation Name`;
an unqualified name or configured default schema is not a physical identity
witness. The catalog query and explain are both counted as diagnostic collection
commands. If namespace or complete tree mapping is unavailable, the structured
index-choice claim is withheld too; legacy explain assertions remain separate.
Namespace strings remain provider-private and are not copied into evidence.
The closed PostgreSQL tree requires unary relational inputs for limit, sort,
materialize and aggregation nodes, and no relational inputs for access or
function-produced row sources. Native `SubPlan` and `InitPlan` children are
preserved as nested operator structure without counting them as relational
inputs. In particular, ordinal-string emission may produce an aggregate over
a function-produced source beneath an access node. These computational nodes
have no fabricated storage target. Parentage represents native nesting, not
an inferred execution dependency or a claim about function internals.
Every non-root PostgreSQL child must supply a recognized native relationship;
only `Outer` is a supported ordinary input in this closed mapping. Missing,
unknown or incompatible relationships withhold the forest. Sort-key presence
alone does not identify a sort's purpose, which therefore remains unknown.
A present but malformed index condition is unsupported,
not an absent condition that can be relabelled as an index scan.
The same catalog query also witnesses valid/live physical indexes on that
relation. An implicit index can carry an opaque identity with no invented
logical index name. This is not a uniqueness or primary-key enforcement claim.

Native-plan structure also preserves scalar computation, output projection
and row skipping. The generic
`Compute`, `Projection` and `Offset` native operators retain these stages when
they appear in a provider plan. They carry no storage target, index, covering
or sort-purpose attributes. In particular, a scalar function expression is not
a function-produced row source (`FunctionScan`), and an offset operator alone
does not prove its numeric row bound. Expression bodies and values remain
provider-private. These structural operators do not attest to function
internals, spill behavior, runtime costs or observed row counts.

Ordering evidence preserves semantic order separately from physical transforms
such as null ranks and ordinal-string length companions. Any uniqueness claim
must identify its provider-enforced key and scope, not infer uniqueness from a
column's name. Missing native detail must remain unknown rather than be filled
with the requested order or expected index.
In particular, relational point reads currently have no native LIMIT even
though they materialize at most one row. A key-derived cardinality bound, its
enforced uniqueness witness, native LIMIT and client materialization behavior
are separate facts. MongoDB's actual point-read limit of one does not imply a
corresponding relational limit.

### Redaction boundary

The public evidence contains no query constants, parameter values, raw tenant
or scope strings, continuation tokens, credentials, connection information,
provider commands or native plan payloads. Value-free binding identities may
show that two emitted terms share one binding, but never encode its value.

Physical names require care: a per-scope collection or index name can itself
encode a scope. Report safe declared identities and capture-local opaque
physical identities; do not export raw scope-bearing names or an unsalted hash
of secret/low-entropy values. Opaque invocation/artifact identities correlate
diagnostic material without turning its contents into public evidence.
Physical identity equality is defined only inside one explicitly bounded
capture. Tokens are freshly assigned per capture, never copied from a MongoDB
scope suffix, and are not stable identifiers across captures. Declared unit and
index identities provide the cross-capture comparison surface.

Existing raw command/explain diagnostics remain a separate privileged surface
with their existing sensitivity. Retaining them does not make them redacted or
appropriate to serialize into the structured contract. Consumers need no raw
artifact access to validate a supported structured shape.

### Consumer artifact persistence

The immutable observation types are an in-memory contract, not a default JSON
wire format. A consumer that persists observations must explicitly map the
actual callback facts into its versioned artifact schema, retain unknown versus
absent versus explicit bounds, and validate the reloaded artifact before using
it for acceptance. This mapping must not reconstruct facts from a query request
or interpret native command/plan text. Artifact provenance, hashes and workload
policy remain consumer-owned. In particular, default reflection-based JSON
round-tripping is not supported: constructor collection types and private bound
construction do not provide a lossless deserialization contract.

## Alternatives considered

- Consumer-owned SQL/BSON parsing: rejected because provider grammar and plan
  interpretation would remain duplicated outside their owner.
- An observer that projects only the incoming query request: rejected because
  it can attest to scope, ordering or a limit the native emitter omitted.
- Copying raw query ASTs, commands or plans into a nominally structured wrapper:
  rejected because it leaks values and preserves the parsing dependency.
- A universal acceptance-policy engine in Groundwork: rejected because provider
  correctness does not own application-specific budgets or workload exceptions.
- Replacing the existing round-trip observer: unnecessary compatibility break;
  counting remains useful without expensive plan collection.

## Verification and delivery

Before accepting this ADR, prove one bounded producer-to-consumer route using
only the proposed structured interface. Then cover all four native providers,
including point reads and the consumer's remaining bounded shapes. Tests must
exercise string equality, scope binding, nullable ordering, native lookahead,
winning-vs-rejected plans, index/scan/sort classification, cancellation,
unsupported/malformed plans, multi-statement correlation and redaction.

Mutation tests must demonstrate that omitted scope/limit/order facts, a wrong
index, a spill, a different statement and requested-but-not-emitted semantics
cannot pass an evidence consumer. Legacy command-count tests remain unchanged.
Unknown facts must fail closed for any consumer policy that requires them.

Publish the contract with API documentation, a release note and migration
guidance. Verify an external consumer against the exact released package and
remove its shadow command parser; a source-project reference alone does not
prove the migration. Keep correctness, concurrency, native-plan evidence,
timing measurements and application acceptance as distinct gates.
