# Proposal: 修复关系成员名的过度后缀剥离

`BusinessOntologySemanticProjectorTests.NormalizesStructuralRelationMemberNamesAsync` 当前可独立复现失败：`Record` 被当作技术 carrier 后缀，在移除 `EntityList` 后再次剥离。该修复只调整 relation-member 后缀词表，保留 `Records` 的集合归并能力，不扩展业务语义推导范围。
