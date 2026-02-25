# Design：all-MiniLM-L6-v2 ONNX Provider

## 上下文

`sentence-transformers/all-MiniLM-L6-v2` 是小型 sentence embedding 模型，常见输出维度 384，适合本地代码/文档语义检索。

## 方案概览

1. 模型资产
   - `Models/all-MiniLM-L6-v2/model.onnx`
   - `Models/all-MiniLM-L6-v2/tokenizer.json`
   - `Models/all-MiniLM-L6-v2/vocab.txt`
   - config files
2. Provider
   - `OnnxMiniLmEmbeddingProvider`
   - 加载 tokenizer 和 ONNX model
   - 输入：文本
   - 输出：384 维 `EmbeddingVector`
3. Pooling
   - 使用 attention mask 对 token embeddings mean pooling。
   - L2 normalize。
4. 默认 provider
   - `CozoVectorSearchService` 默认调用 `EmbeddingProviderFactory.CreateDefault()`。
   - 资产存在时使用 ONNX；缺失时使用 deterministic。

## 风险 / 权衡

- 包体积增加。
- ONNX Runtime native dependency 增加平台复杂度。
- tokenizer API 如果不稳定，需要保留 fallback 或最小 WordPiece tokenizer。
