# Decisions

## Usage
- mission G1 契约已定；执行期决策追加至此

## ED-1 公开面收敛：FTS 通道 API 全部 internal，跨程序集走 InternalsVisibleTo（2026-07-05）

- **决策**：`TextSearchHit`、`TextSearchAsync`、`EnsureSearchTextAsync`、`EnsureFtsIndexAsync` 定为 internal；VectorSearch csproj 增加 `InternalsVisibleTo` 到 Cozo.DotNet.LlmWiki.Indexing（BuildSearchIndexStepAsync 调用两个 Ensure 方法）与 Cozo.DotNet.LlmWiki.Tests（既有各包对 Tests 的惯例一致）。公开面仅保留 `HybridSearchAsync` 新服务方法与模型 add-only 字段（RrfScore/Channels/Mode/Diagnostics）。
- **理由**：与 design.md「最小公开面」承诺对齐——FTS 通道是 hybrid 检索的内部实现细节，无跨程序集外部调用方；缩小公开面降低后续演进成本。静态 internal 方法的判断保留：Ensure* 保持 static，Indexing 无需实例化服务、不触发 ONNX embedding provider 加载。
