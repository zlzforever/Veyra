# MySQL Sync

基于 .NET 10 和 MySqlConnector 的 MySQL 多表复制工具：源库流式读取，目标库按批次执行参数化多行 `INSERT IGNORE`。目标表的主键/唯一键冲突会自动跳过，表结构需要预先保持一致。

## 运行

```bash
dotnet restore
dotnet run -- \
  --source "Server=127.0.0.1;Port=3306;Database=source_db;User ID=root;Password=...;" \
  --destination "Server=127.0.0.1;Port=3306;Database=target_db;User ID=root;Password=...;" \
  --tables users,orders
```

复制源库全部普通表：

```bash
dotnet run -- --source "..." --destination "..." --all-tables --batch-size 2000 --parallelism 4
```

也可以使用环境变量配置，命令行参数优先级更高：

```bash
export MYSQL_SYNC_SOURCE='Server=127.0.0.1;Database=source_db;User ID=root;Password=...;'
export MYSQL_SYNC_DESTINATION='Server=127.0.0.1;Database=target_db;User ID=root;Password=...;'
export MYSQL_SYNC_TABLES='users,orders'
export MYSQL_SYNC_BATCH_SIZE=2000
export MYSQL_SYNC_PARALLELISM=4
dotnet run
```

可用变量：`MYSQL_SYNC_SOURCE`、`MYSQL_SYNC_DESTINATION`、`MYSQL_SYNC_TABLES`、`MYSQL_SYNC_ALL_TABLES`、`MYSQL_SYNC_BATCH_SIZE`、`MYSQL_SYNC_PARALLELISM`、`MYSQL_SYNC_VERBOSE`。`MYSQL_SYNC_ALL_TABLES` 和 `MYSQL_SYNC_VERBOSE` 使用 `true/false`。

`--batch-size` 越大，网络往返越少，但单条 SQL 和内存占用越大；通常 500–5000 合适。`--parallelism` 建议从 2–4 开始，避免同时写入过多导致目标库锁竞争。连接字符串可按需加入 `Compression=true`、`Keepalive=30` 等 MySqlConnector 参数。

注意：`INSERT IGNORE` 会跳过所有被 MySQL 判定为 warning 的行，不仅是唯一键冲突。如果需要严格只忽略重复键，应改用按唯一键生成的 `ON DUPLICATE KEY UPDATE` 方案。
