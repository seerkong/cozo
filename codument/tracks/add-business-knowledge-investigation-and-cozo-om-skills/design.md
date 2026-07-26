# Design: Two-stage business knowledge workflow

`investigate-business-knowledge-db` owns evidence access and interpretation artifacts. It uses `depa-wiki investigate` only, follows every evidence cursor, and emits a ledger with evidence IDs, confidence, alternatives, and gaps.

`model-business-knowledge-cozo-om` owns executable implementation. It accepts only the reviewed ledger, uses public `cozo-om` APIs, declares a local `file:` dependency on `cozo-lib-bun`, and verifies a real in-memory CozoDB package.

The ledger is the explicit boundary: neither raw database rows nor model-generated text jump directly into executable schema.
