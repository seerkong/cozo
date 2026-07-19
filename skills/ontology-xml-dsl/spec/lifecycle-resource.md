# Lifecycle resources

A `LifecycleModule` owns state-machine meaning independently from controller methods, UI pages, or event-handler names.

```xml
<LifecycleModule id="FieldService.Lifecycles.Core">
  <StateMachines>
    <StateMachine
      id="FieldService.Lifecycle.WorkOrder"
      subject="FieldService.WorkOrder"
      stateProperty="workflowState"
      initial="draft">
      <Description>Core work order lifecycle</Description>
      <States>
        <State id="draft" />
        <State id="scheduled" />
        <State id="inProgress" />
        <State id="completed" terminal="true" />
      </States>
      <Transitions>
        <Transition id="FieldService.Transition.Schedule" action="schedule" from="draft" to="scheduled">
          <EvidenceRefs>
            <EvidenceRef ref="backend:schedule-transition" />
          </EvidenceRefs>
        </Transition>
        <Transition id="FieldService.Transition.StartWork" action="startWork" from="scheduled" to="inProgress">
          <Guard>
            <PropertyEquals property="dispatchState" value="confirmed" />
          </Guard>
          <Effects>
            <SetProperty property="workflowState" value="inProgress" />
          </Effects>
          <EvidenceRefs>
            <EvidenceRef ref="backend:start-work-transition" />
          </EvidenceRefs>
        </Transition>
      </Transitions>
      <EvidenceRefs>
        <EvidenceRef ref="contract:work-order-lifecycle" />
      </EvidenceRefs>
    </StateMachine>
  </StateMachines>
</LifecycleModule>
```

## Grammar

- `LifecycleModule` requires FQN `id`; it contains optional `Description`, then exactly one `StateMachines`.
- `StateMachine` requires FQN `id`, FQN `subject`, lowerCamelCase `stateProperty`, and local `initial` state ID.
- Optional `status` is `accepted|hypothesis`.
- Direct children are optional `Description`, exactly one `States`, exactly one `Transitions`, optional `EvidenceRefs`.
- `State` requires unique local `id`; optional `terminal` is `true|false`; it has optional `Description` only.
- `initial` must name a declared state.
- `Transition` requires FQN `id`, lowerCamelCase `action`, and declared local `from` and `to` state IDs.
- Optional `Transition@status` is `accepted|hypothesis`.
- A transition contains optional `Description`, optional `Guard`, optional `Effects`, optional `ImplementationRef`, and optional `EvidenceRefs`.
- `Guard` contains exactly one predicate from the rule predicate vocabulary.
- `Effects` contains one or more declarative effects.
- Accepted transitions require evidence. Hypothesis transitions are not projected as executable actions by default.
- Accepted and hypothesis StateMachines require their own `EvidenceRefs`; Transition refs may use the StateMachine evidence only when it unambiguously covers that transition.

## Effect vocabulary

```xml
<SetProperty property="workflowState" value="inProgress" />
<ClearProperty property="dispatchNote" />
<CreateRelation relation="FieldService.Relation.AssignedTo" targetRef="subject.requestedTechnician" />
<RemoveRelation relation="FieldService.Relation.QueuedFor" targetRef="transition.previousQueue" />
```

Effects describe intended state change; they are not host-language code. `targetRef` is a typed projection reference whose supported vocabulary must be defined by the consuming compiler. Use `ImplementationRef` when a transition needs runtime behavior not expressible by the standard effects.

Semantic validation checks duplicate transitions, missing states, states unreachable from `initial`, impossible guards, and effects inconsistent with the destination state.
