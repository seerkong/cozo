# 变更：随包分发 all-MiniLM-L6-v2 ONNX embedding

## 背景和动机 (Context And Why)

当前 VectorSearch 使用 deterministic hash embedding，只能证明接口和 Cozo 向量路径可用，不具备真实语义效果。用户已拍板选择 `sentence-transformers/all-MiniLM-L6-v2`，并要求随包分发。

## "要做"和"不做" (Goals / Non-Goals)

**目标:**
- 下载并随包保存 `all-MiniLM-L6-v2` ONNX 模型资产。
- 新增 `OnnxMiniLmEmbeddingProvider`。
- 使用 ONNX Runtime 本地推理。
- 使用 tokenizer + mean pooling + L2 normalize，输出 384 维 embedding。
- 让 VectorSearch 默认优先使用 bundled ONNX provider，资产缺失时回退 deterministic provider。
- 补 smoke test 验证 provider 能产出 384 维向量。

**非目标:**
- 不接 OpenAI/Ollama API。
- 不支持任意 HuggingFace 模型自动加载。
- 不实现 GPU/CUDA provider。
- 不做量化模型选择策略。

## 变更内容（What Changes）

- VectorSearch 增加 ONNX Runtime / tokenizer 依赖。
- VectorSearch 增加 `Models/all-MiniLM-L6-v2/` 资产。
- VectorSearch 增加 `OnnxMiniLmEmbeddingProvider`。
- Tests 增加 ONNX provider smoke。
- README 更新本地 embedding 说明。

## 影响范围（Impact）

- 受影响能力：`dotnet-llm-wiki-onnx-embedding`
- 受影响代码：
  - `cozo-lib-dotnet-llm-wiki/packages/Cozo.DotNet.LlmWiki.VectorSearch/**`
  - `cozo-lib-dotnet-llm-wiki/tests/**`
