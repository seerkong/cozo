# Rule resources

A `RuleModule` owns declarative domain constraints. It does not own host-language callback bodies.

```xml
<RuleModule id="FieldService.Rules.Dispatch">
  <Rules>
    <Rule
      id="FieldService.Rule.DispatchRequiresQualification"
      scope="FieldService.WorkOrder"
      kind="CrossEntity">
      <Statement>A scheduled work order is assigned only to a qualified technician.</Statement>
      <When>
        <PropertyEquals property="workflowState" value="scheduled" />
      </When>
      <Require>
        <EveryRelated relation="FieldService.Relation.AssignedTo">
          <PropertyEquals property="qualificationState" value="qualified" />
        </EveryRelated>
      </Require>
      <Violation code="TECHNICIAN_NOT_QUALIFIED" message="Technician is not qualified for dispatch." />
      <EvidenceRefs>
        <EvidenceRef ref="backend:dispatch-qualification-check" />
      </EvidenceRefs>
    </Rule>
  </Rules>
</RuleModule>
```

## Rule grammar

- `RuleModule` requires FQN `id`; it contains optional `Description`, then exactly one `Rules`.
- `Rule` requires FQN `id`, FQN `scope`, and `kind`.
- Allowed kinds are `Conditional`, `CrossEntity`, `ComputedDependency`, `Existential`, `Uniqueness`, `Cardinality`, and `Custom`.
- Optional `status` is `accepted|hypothesis`.
- Direct children are exactly one `Statement`, optional `When`, exactly one `Require`, exactly one `Violation`, optional `RuntimeBinding`, and optional `EvidenceRefs`, in that order.
- `Statement` is non-empty plain text. It explains but does not execute the rule.
- `When` contains exactly one predicate. Missing `When` means unconditional.
- `Require` contains exactly one predicate.
- `Violation` requires non-empty `code` and `message`, with no children.
- Accepted rules require at least one evidence reference unless their source is an explicitly designated ontology policy file.
- `Custom` requires `RuntimeBinding`; other kinds may use it only when the current C# OM projection cannot interpret the predicate directly.

## Predicate vocabulary

Every predicate is a typed XML element. Do not use expression strings.

### Boolean composition

```xml
<All>predicate predicate...</All>
<Any>predicate predicate...</Any>
<Not>predicate</Not>
```

- `All` and `Any` contain at least two predicates.
- `Not` contains exactly one predicate.

### Property predicates

```xml
<PropertyPresent property="workOrderNumber" />
<PropertyEquals property="workflowState" value="scheduled" />
<PropertyNotEquals property="workflowState" value="cancelled" />
<PropertyIn property="priority">
  <Value value="normal" />
  <Value value="urgent" />
</PropertyIn>
<PropertyCompare property="estimatedMinutes" op="gt" value="0" />
```

`PropertyCompare@op` is one of `lt|lte|gt|gte`. Scalar `value` text is converted through the declared property type during semantic validation.

### Type and relation predicates

```xml
<TypeIs type="FieldService.EmergencyWorkOrder" />
<RelatedExists relation="FieldService.Relation.AssignedTo">
  <TypeIs type="FieldService.Technician" />
</RelatedExists>
<EveryRelated relation="FieldService.Relation.AssignedTo">
  <PropertyEquals property="qualificationState" value="qualified" />
</EveryRelated>
<RelatedCount relation="FieldService.Relation.AssignedTo" op="lte" value="1" />
```

`RelatedExists` and `EveryRelated` contain exactly one predicate evaluated in target-entity context. `RelatedCount@op` is `eq|neq|lt|lte|gt|gte` and `value` is a non-negative integer.

### Existential predicate

```xml
<ExistsRelated
  relation="FieldService.Relation.AssignedTo"
  direction="out"
  targetType="FieldService.Technician" />
```

`direction` is `out|in`. This maps to the current OM existential rule shape when no richer predicate is required.

## Runtime binding

```xml
<RuntimeBinding ref="FieldService.Implementation.DispatchQualificationConstraint" />
```

The referenced implementation mapping identifies externally owned runtime code. XML must not contain source code, method bodies, callback text, or arbitrary CozoScript.
