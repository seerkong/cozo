# Decisions

## Usage
- mission 契约已定；执行期决策追加至此

### 1. 【P1】trace Direction 收窄（执行期）
- proposal 目标含 Direction(down/up) 选项；design/实现收敛为 up=调用方交换 from/to，不加反向投影，TraceOptions 不设 Direction 字段（语义等价、投影面最小）。
- 状态：decided
