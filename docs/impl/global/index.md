---
knowledge_plane: global
doc_role: guide
status: active
last_verified: 2026-07-01
---

# Global Implementation Knowledge

## 目录职责

- **holds**：跨实现平面的架构、维护、规则、参考和排障知识。
- **excludes**：领域本体放在 `docs/modeling/`；一次性实现轨迹放在 `codument/tracks/`。
- **tier**：`stable`
- **promotes_from**：稳定后的实现经验、源码核对、测试验证和 track reports
- **promotes_to**：后续实现、测试、review 与排障流程

| Category | Responsibility |
|----------|----------------|
| [overview](overview/index.md) | 跨实现主题的心智模型和维护总览 |
| [howto](howto/index.md) | 可重复的维护操作：索引运维、DEPA 规则扩展、eval 跑法 |
| [reference](reference/index.md) | 查表与外部真源指针（MCP 工具速查） |
| [troubleshooting](troubleshooting/index.md) | 故障模式与排障（hooks 接入） |

