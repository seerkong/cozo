# Cozo .NET Packages

本目录存放独立的 .NET 子项目，可被 Cozo .NET 主绑定、OM capsule、server 或 agent 集成层引用。

## Datalog.Core

路径：`packages/Datalog.Core/Cozo.DotNet.Datalog.Core.csproj`

`Datalog.Core` 是与执行后端无关的 Portable Datalog 语言包，提供：

- program、rule、query、atom、literal、term 的 AST 模型。
- 面向 v1 Portable Datalog 小子集的 parser。
- validator 安全检查。
- normalized IR。
- 结构化 diagnostics，包含 severity、code、message，以及可选 source span。

它不引用 `Cozo.DotNet.csproj`、`CozoDb`、Cozo native runtime 或 `Om.Core`。

## Datalog.Cozo

路径：`packages/Datalog.Cozo/Cozo.DotNet.Datalog.Cozo.csproj`

`Datalog.Cozo` 将已校验的 Portable Datalog IR 编译为参数化 CozoScript，提供：

- portable predicate name 到 Cozo stored relation 的映射。
- inline rule 和 query entry rule 的 CozoScript 生成。
- `$name` 参数项的参数保留，不把用户值内联进脚本。
- 字符串字面量转义。
- backend diagnostics。

它只引用 `Datalog.Core`。

## 示例

Portable Datalog:

```prolog
reachable(X, Y) :- calls(X, Y).
reachable(X, Z) :- reachable(X, Y), calls(Y, Z).
?- reachable($start, Target).
```

当 `calls -> *code_calls{from, to}` 时，编译得到的 CozoScript：

```cozo
reachable[X, Y] := *code_calls{from: X, to: Y}
reachable[X, Z] := reachable[X, Y], *code_calls{from: Y, to: Z}
?[Target] := reachable[$start, Target]
```
