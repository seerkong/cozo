# Design

Public contracts live in `Om.Core` models/inputs and `CozoOm` facade. Parent
updates use an explicit patch discriminant, so null is no longer overloaded.
Validation/traversal/inference delegate to existing core logic with cancellation.
Behavior definition and interceptor registration validate all persistent inputs
before registry effects, compensating if a later persistence step fails.
