# Implementation mapping resources

Implementation mappings connect domain meaning to code observations without turning DTOs, pages, tables, or framework roles into domain types.

```xml
<ImplementationMappingModule id="FieldService.Mappings.Backend">
  <Mappings>
    <ImplementationMapping
      id="FieldService.Implementation.WorkOrder"
      conceptRef="FieldService.WorkOrder"
      status="accepted">
      <RepresentedBy>
        <CodeRef
          repository="field-service-api"
          language="java"
          kind="dto"
          symbol="example.fieldservice.api.WorkOrderDto" />
      </RepresentedBy>
      <ImplementedBy>
        <CodeRef
          repository="field-service-api"
          language="java"
          kind="service"
          symbol="example.fieldservice.WorkOrderService" />
      </ImplementedBy>
      <Enforces>
        <RuleRef ref="FieldService.Rule.DispatchRequiresQualification" />
      </Enforces>
      <EvidenceRefs>
        <EvidenceRef ref="backend:work-order-service" />
      </EvidenceRefs>
    </ImplementationMapping>
  </Mappings>
</ImplementationMappingModule>
```

## Grammar

- `ImplementationMappingModule` requires FQN `id`; it contains optional `Description`, then exactly one `Mappings`.
- `ImplementationMapping` requires FQN `id`; exactly one of `conceptRef`, `ruleRef`, `lifecycleRef`, or `relationRef` identifies the interpreted ontology object.
- Optional `status` is `accepted|hypothesis`.
- Allowed children are optional `Description`, `RepresentedBy`, `ImplementedBy`, `PresentedBy`, `StoredBy`, `ExposedBy`, `Enforces`, and `EvidenceRefs`, in that order.
- Each role collection contains one or more `CodeRef` entries.
- `Enforces` contains one or more empty `RuleRef ref="RuleFqn"` entries.
- Accepted mappings require evidence; inferred mapping evidence retains its grade and confidence.

## CodeRef

`CodeRef` requires:

- `repository`: stable repository key, not an absolute path;
- `language`: normalized language ID such as `java`, `typescript`, `sql`, or `http`;
- `kind`: bounded role such as `controller`, `route`, `service`, `repository`, `mapper`, `listener`, `handler`, `entity`, `dto`, `vo`, `frontend-page`, `frontend-form`, `api`, or `table`;
- `symbol`: stable symbol key, API identity, or table identity.

Optional attributes are `path`, `resolver`, and `confidence`. Prefer evidence for volatile paths and line positions. A Java Spring-derived role remains implementation evidence; the mapping is the explicit interpretation boundary.

## Runtime implementations

A mapping may identify a C# implementation binding:

```xml
<ImplementationMapping
  id="FieldService.Implementation.DispatchQualificationConstraint"
  ruleRef="FieldService.Rule.DispatchRequiresQualification">
  <ImplementedBy>
    <CodeRef
      repository="ontology-runtime"
      language="csharp"
      kind="constraint-handler"
      symbol="FieldServiceOntologyRules.ValidateDispatchQualificationAsync" />
  </ImplementedBy>
  <EvidenceRefs>
    <EvidenceRef ref="runtime:dispatch-constraint-test" />
  </EvidenceRefs>
</ImplementationMapping>
```

The XML references the symbol but never embeds its body.

Implementation mappings may consume direct source observations or `ck_*` facts through evidence, but must not reference or depend on `depa_*` objects.
