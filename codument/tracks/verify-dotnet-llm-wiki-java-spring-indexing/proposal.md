# Proposal: Java/Spring dogfood verification

## Goal

Prove the new C# depa-wiki Java/Spring pipeline on `/Users/kongweixian/java/ks-ep/is-asset-new` with a Release binary, real SQLite storage, bounded graph queries, and representative Spring fact samples.

## Safety

Use an explicit database under `/tmp` so the target repository's existing `.depa-wiki` state is not overwritten. Persist durable evidence as track/mission reports, not as a large database artifact in this repository.
