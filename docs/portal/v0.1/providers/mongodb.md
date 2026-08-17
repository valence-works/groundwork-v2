---
title: MongoDB provider
---

# MongoDB provider

Install `Groundwork.MongoDb` and create a connection with
`MongoProviderFactory`. Use a replica set or sharded cluster when the application
requires atomic commit, exact append, or transactional staged writes.

Standalone MongoDB deliberately omits transaction-dependent capability
descriptors and refuses those operations. Do not infer support merely because a
session can be opened.

Groundwork maps scoped units to provider-owned physical collections and uses
native indexes, aggregation pipelines, and explain plans. Run conformance against
the exact topology; topology determines the advertised capability set.
