# Before and after

The "after" column in every pair below is **verbatim** from the corpus
(`docs/wiki/`, `AGENTS.md`, `README.md`, `docs/v2/releases/`, or a commit
subject in `valence-works/groundwork-v2`). The "before" is the same content
written in default assistant register, for contrast. Read them as the target,
not as templates to fill.

---

## 1. FAQ answer — lead with the verdict (rules 1, 2, 7)

**Before**

> When it comes to storing floating-point values, there are a few important
> things to consider. Groundwork does support the `Double` type, and it works
> across all of the supported providers. However, it's worth noting that
> comparison operations are generally not supported, as floating-point
> behaviour can vary between different database systems. You may want to
> consider using an alternative type for values you need to query on.

**After**

> Yes — `PortableType.Double` is storable on all four providers and round-trips
> bit-for-bit. You cannot *compare* one. Binary floating-point comparison,
> rounding, and index behavior differ across stores, so an index seek and a scan
> can disagree about the same row; predicates and ordering are refused
> (`GW-SEM-TYPE-006`), and a key, index, or grouping column is refused at
> declaration (`GW-PORT-012`). Use `Int32`, `Int64`, or declared `Decimal(18,4)`
> for a value you query on.

What changed: the verdict moved to word one; "a few important things to
consider" and "generally" are gone; both diagnostic codes are named inline; the
mechanism ("an index seek and a scan can disagree") replaces the vague "behaviour
can vary"; the recommendation is three concrete types rather than "an
alternative type".

---

## 2. Concept definition — the rejected alternative (rules 2, 4, 8)

**Before**

> The logical identifier is used to maintain identity when a rename occurs.
> Schema planning uses it for slot keys, which means that if you change the
> `Name` while keeping the `Id` the same, the system will handle it as a rename
> operation and your data will be preserved.

**After**

> The logical id is what **carries identity across a rename**. Schema planning
> keys its slots on it, so changing `Name` while keeping `Id` plans as a rename
> that brings the rows with it, rather than as a drop and a create.

What changed: 41 words to 34; the bold marks the phrase the paragraph exists to
establish; "your data will be preserved" becomes the concrete "brings the rows
with it"; and the rejected alternative — a drop and a create — is named, which
is what makes the sentence informative rather than reassuring.

---

## 3. Release note — required action in bold (rules 1, 4, 13)

**Before**

> Due to the changes described above, catalogs created by earlier previews are
> no longer compatible. Users are encouraged to recreate their catalogs from
> their current declarations. Unfortunately, no migration path is available at
> this time.

**After**

> A catalog created by an earlier preview therefore cannot be reconciled with
> these declarations, and re-applying is not the remedy — the tool refuses to
> read that catalog at all.
>
> **Discard every catalog created by an earlier preview and create a fresh one
> from the current declarations.** Groundwork ships no in-place migration,
> compatibility alias, dual-write, or fallback path between preview catalogs.

What changed: "users are encouraged to" becomes a bolded imperative on its own
line; "unfortunately" and "at this time" — both apologies for a deliberate
decision — are gone; the tool "refuses", it does not fail; and the absent
migration path is enumerated as four specific things that do not exist rather
than one vague absence.

---

## 4. Operational rule — the cost of breaking it (rules 5, 13)

**Before**

> Please remember to unassign yourself if you stop working on an issue. This
> helps keep the issue board accurate for everyone.

**After**

> **To release it**, unassign and say why. Leaving a claim behind when you stop
> is worse than never making one, because the issue then looks worked when it
> is not.

What changed: "please remember to" becomes a bolded directive; the vague benefit
("helps keep the board accurate") becomes the specific failure the rule prevents,
and the comparison — worse than never claiming at all — is what makes the rule
stick.

---

## 5. Document opening (rule 10)

**Before**

> This document provides an overview of the core concepts in Groundwork. It
> covers storage units, contract families, and the key abstractions you'll need
> to understand in order to work effectively with the library.

**After**

> Read this once and most of Groundwork's API stops being surprising.

What changed: everything. The "before" describes the document; the "after"
states what the reader gets from it. A table of contents already lists the
sections, so listing them in prose is duplication.

---

## 6. Commit subjects (rule 14)

| Before | After |
| --- | --- |
| `fix: connection leak on dispose failure` | `Never let a failed disposal strand the connection it was releasing` |
| `feat(concurrency): add timeout and better logging` | `Bound the concurrency job and make a hang name itself` |
| `chore: clean up unused test databases` | `Reclaim abandoned live-provider databases` |
| `fix(sqlserver): use correct parameter limit` | `Give the session-only SQLite executor SQLite's own parameter budget` |
| `refactor: make key coverage derivation shared` | `Count the declared key as query coverage, from one shared derivation` |

The pattern in the last one: verb, object, comma, then the clause that pins down
the precise qualification. The prefix and scope tag carry no information a
reader of the subject line needs, and they cost the words that would have
carried the qualification.
