# Type and relation resources

## TypeModule

```xml
<TypeModule id="FieldService.Types.WorkOrders">
  <Description>Work order identity and shared traits</Description>
  <MixinDeclarations>
    <Mixin id="FieldService.Mixin.Auditable">
      <Description>Records audit ownership and time</Description>
      <Attributes>
        <Attribute name="updatedAt" type="Validity" required="true">
          <EvidenceRefs>
            <EvidenceRef ref="contract:audit-fields" />
          </EvidenceRefs>
        </Attribute>
      </Attributes>
      <EvidenceRefs>
        <EvidenceRef ref="contract:audit-fields" />
      </EvidenceRefs>
    </Mixin>
  </MixinDeclarations>
  <Types>
    <Type id="FieldService.WorkItem" abstract="true">
      <Description>A schedulable unit of field work</Description>
      <EvidenceRefs>
        <EvidenceRef ref="contract:work-item-schema" />
      </EvidenceRefs>
    </Type>
    <Type id="FieldService.WorkOrder" parent="FieldService.WorkItem">
      <Description>An accepted request for field work</Description>
      <Mixins>
        <MixinRef ref="FieldService.Mixin.Auditable" />
      </Mixins>
      <Attributes>
        <Attribute name="workOrderNumber" type="String" required="true">
          <Description>Stable work order number</Description>
          <EvidenceRefs>
            <EvidenceRef ref="database:work-order-number-key" />
          </EvidenceRefs>
        </Attribute>
        <Attribute name="workflowState" type="String" required="true">
          <EvidenceRefs>
            <EvidenceRef ref="contract:work-order-schema" />
          </EvidenceRefs>
        </Attribute>
      </Attributes>
      <ComputedAttributes>
        <ComputedAttribute name="isDispatchable" type="Bool">
          <Description>Whether the work order can be scheduled</Description>
          <ImplementationRef ref="FieldService.Implementation.Dispatchability" />
          <EvidenceRefs>
            <EvidenceRef ref="backend:dispatchability-policy" />
          </EvidenceRefs>
        </ComputedAttribute>
      </ComputedAttributes>
      <EvidenceRefs>
        <EvidenceRef ref="contract:work-order-schema" />
      </EvidenceRefs>
    </Type>
  </Types>
</TypeModule>
```

### Module and Mixin grammar

- `TypeModule` requires FQN `id`; direct children are optional `Description`, optional `MixinDeclarations`, then optional `Types`, in that order. At least one declaration container is required.
- `MixinDeclarations` contains one or more `Mixin` declarations. `Types` contains one or more `Type` declarations.
- `Mixin` requires FQN `id`; optional `status` is `accepted|hypothesis`.
- Direct `Mixin` children are optional `Description`, optional `Mixins`, optional `Attributes`, and optional `EvidenceRefs`, in that order.
- `Mixins` contains one or more empty `MixinRef ref="MixinFqn"` children. It is used by both Mixin declarations and Type declarations.
- Mixin attributes use the same `Attribute` grammar as Type attributes. A Mixin cannot declare a parent, computed attribute, relation endpoint, lifecycle, or executable behavior.
- `MixinRef@ref` must resolve to a Mixin declaration, never a Type. Duplicate refs, self refs, and direct or transitive Mixin composition cycles are invalid.

### Type grammar

- `Type` requires FQN `id`; optional attributes are `parent`, `abstract`, and `status`.
- `parent` refers to another type ID. `abstract` is `true|false`. `status` is `accepted|hypothesis`.
- Direct `Type` children are optional `Description`, optional `Mixins`, optional `Attributes`, optional `ComputedAttributes`, and optional `EvidenceRefs`, in that order.
- `Attributes` contains `Attribute` elements with unique lowerCamelCase `name`, required `type`, and required `required="true|false"`.
- Allowed primitive types are `String`, `Number`, `Bool`, `Json`, and `Validity`. A FQN type denotes an object-valued declaration only when a projection supports it; prefer relations for domain object links.
- `Attribute` may contain one `Description` and `EvidenceRefs`.
- `ComputedAttribute` requires `name` and `type`; it declares a computed value but not executable code. It contains optional `Description`, exactly one `ImplementationRef`, and optional `EvidenceRefs`.
- A type cannot inherit from itself or from a Mixin. Semantic validation rejects inheritance cycles, Mixin cycles, unknown reference kinds, and incompatible attributes inherited or composed from multiple owners.

## RelationModule

```xml
<RelationModule id="FieldService.Relations.Dispatch">
  <Relations>
    <Relation
      id="FieldService.Relation.AssignedTo"
      name="assignedTo"
      from="FieldService.WorkOrder"
      to="FieldService.Technician"
      directed="true"
      min="0"
      max="1">
      <Description>Current technician assignment</Description>
      <Properties>
        <Property name="assignedAt" type="Validity" required="true">
          <EvidenceRefs>
            <EvidenceRef ref="database:assignment-timestamp" />
          </EvidenceRefs>
        </Property>
        <Property name="assignmentRole" type="String" required="false">
          <EvidenceRefs>
            <EvidenceRef ref="contract:dispatch-assignment" />
          </EvidenceRefs>
        </Property>
      </Properties>
      <EvidenceRefs>
        <EvidenceRef ref="backend:technician-assignment" />
      </EvidenceRefs>
    </Relation>
  </Relations>
</RelationModule>
```

### Relation grammar

- `RelationModule` requires FQN `id`; direct children are optional `Description`, then exactly one `Relations`.
- `Relations` contains one or more `Relation` elements.
- `Relation` requires FQN `id`, lowerCamelCase `name`, FQN `from`, FQN `to`, and `directed="true|false"`.
- Optional `min` is a non-negative integer. Optional `max` is a non-negative integer or `*`; when both are numeric, `max >= min`.
- Optional `status` is `accepted|hypothesis`.
- Direct children are optional `Description`, optional `Properties`, then optional `EvidenceRefs`, in that order.
- `Properties` contains one or more edge-owned `Property` declarations. `Property` requires unique lowerCamelCase `name`, primitive `type`, and `required="true|false"`; it contains optional `Description` then optional `EvidenceRefs`.
- Relation properties use only `String|Number|Bool|Json|Validity`. A property cannot point to another semantic object; use another Relation for object links.
- Endpoints refer to declared types. Cardinality is closed-world validation intent and must not be inferred from one sample entity.
- Relation identity is independent from endpoint implementation fields or join-table names.
- Edge-owned properties describe the association occurrence. If the association has independent identity, is referenced by other relations, or owns a lifecycle, model it as a Type with ordinary Attributes and connect it through Relations.

## Shared child grammar

`EvidenceRefs` contains one or more empty `EvidenceRef ref="evidence-id"` children. Do not place path, line, confidence, or source text here.

Accepted and hypothesis Type, Mixin, and Relation interpretations require their own `EvidenceRefs`. Attribute, ComputedAttribute, and Property interpretations require evidence coverage and may omit local refs only when the owning Type, Mixin, or Relation evidence unambiguously covers that child claim; generators should prefer object-local evidence for reviewability.
