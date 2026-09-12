# 设计

CozoDb 是 native handle 单写生命周期 owner；close 幂等，存在进行中的查询时拒绝关闭，空闲打开事务在关闭数据库时回滚。CozoTx 只允许一个进行中的查询，防止底层共享 reply channel 把结果交给错误调用者。commit/abort 只允许 open 状态且无待完成查询；终态后拒绝复用。错误保留原始 code、display、ok 等字段并成为 Error。超时使用 Cozo :timeout 原生查询预算，测试要求超时后仍可读写；不把 JS Promise.race 伪装为查询取消。
