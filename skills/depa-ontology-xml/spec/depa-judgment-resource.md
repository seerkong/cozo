# DEPA Judgment Sidecar

`judgments/depa-judgments.xml` is a generated DEPA extension sidecar. It intentionally sits beside, rather than inside, the base `ontology.xml` module graph because a runtime judgment is not automatically a canonical domain type, relation, or business rule.

```xml
<DepaJudgments repository="source-repository">
  <Judgment id="depa:impl:example" type="depa_impl" label="Example">
    <Property name="assigned_by" json="&quot;config&quot;" />
    <Property name="path" json="&quot;src/main/java/example/Example.java&quot;" />
  </Judgment>
  <ConformanceReport>
    <Rule dimension="layering" id="V-L2" verdict="BLOCKED" blockedReason="missing annotation" />
  </ConformanceReport>
</DepaJudgments>
```

Rules:

- `Judgment@id`, `@type`, and `@label` preserve the runtime identity and display label.
- `Property@json` is an exact JSON scalar or object serialization; consumers must parse it as JSON rather than reinterpret it as prose.
- `ConformanceReport` appears only when export ran after an explicit scan. `BLOCKED` is lack of evidence, never a violation or pass.
- The sidecar may link to evidence IDs in future revisions, but it must not duplicate absolute paths, source text, or hidden inference.
- A review may promote a judgment into canonical ontology XML only through an explicit mapping and evidence review. Generated sidecar rows are not a bulk import format.
