using System.Data;
using System.Diagnostics;
using System.Text;
using MySqlConnector;

SyncOptions options;
try
{
    options = SyncOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"参数错误: {ex.Message}");
    SyncOptions.PrintHelp();
    Environment.ExitCode = 2;
    return;
}

if (options.ShowHelp)
{
    SyncOptions.PrintHelp();
    return;
}

try
{
    await new MySqlSynchronizer(options).RunAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine($"同步失败: {ex.Message}");
    if (options.Verbose)
        Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

sealed class SyncOptions
{
    public required string SourceConnectionString { get; init; }
    public required string DestinationConnectionString { get; init; }
    public IReadOnlyList<string> Tables { get; init; } = [];
    public int BatchSize { get; init; } = 1_000;
    public int Parallelism { get; init; } = 4;
    public bool AllTables { get; init; }
    public bool Verbose { get; init; }
    public bool ShowHelp { get; init; }

    public static SyncOptions Parse(string[] args)
    {
        if (args.Contains("--help") || args.Contains("-h"))
            return new SyncOptions { ShowHelp = true, SourceConnectionString = "", DestinationConnectionString = "" };

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"无法识别参数: {args[i]}");
            var key = args[i][2..];
            if (key is "all-tables" or "verbose")
            {
                flags.Add(key);
                continue;
            }
            if (++i >= args.Length)
                throw new ArgumentException($"参数缺少值: --{key}");
            values[key] = args[i];
        }

        var source = Required(values, "source", "MYSQL_SYNC_SOURCE");
        var destination = Required(values, "destination", "MYSQL_SYNC_DESTINATION");
        var allTables = flags.Contains("all-tables") || EnvironmentFlag(values, "all-tables", "MYSQL_SYNC_ALL_TABLES");
        var tables = values.TryGetValue("tables", out var tableValue) ||
                     TryGetEnvironment("MYSQL_SYNC_TABLES", out tableValue)
            ? tableValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        if (!allTables && tables.Length == 0)
            throw new ArgumentException("必须指定 --tables table1,table2 或 --all-tables");
        if (allTables && tables.Length > 0)
            throw new ArgumentException("--tables 与 --all-tables 只能二选一");

        return new SyncOptions
        {
            SourceConnectionString = source,
            DestinationConnectionString = destination,
            Tables = tables,
            AllTables = allTables,
            BatchSize = PositiveInt(values, "batch-size", "MYSQL_SYNC_BATCH_SIZE", 1_000),
            Parallelism = PositiveInt(values, "parallelism", "MYSQL_SYNC_PARALLELISM", 4),
            Verbose = flags.Contains("verbose") || EnvironmentFlag(values, "verbose", "MYSQL_SYNC_VERBOSE")
        };
    }

    public static void PrintHelp() => Console.WriteLine("""
        MySQL 多表复制工具 (.NET 10)

        用法:
          dotnet run -- --source "Server=...;Database=db1;..." --destination "Server=...;Database=db2;..." --tables users,orders
          dotnet run -- --source "..." --destination "..." --all-tables

        参数:
          --source       源 MySQL 连接字符串
          --destination 目标 MySQL 连接字符串
          --tables       要复制的表，逗号分隔；与 --all-tables 二选一
          --all-tables   自动复制源库中的所有 BASE TABLE
          --batch-size   每批行数，默认 1000
          --parallelism  同时复制的表数量，默认 4
          --verbose      输出完整异常堆栈

        环境变量（命令行参数优先）:
          MYSQL_SYNC_SOURCE、MYSQL_SYNC_DESTINATION
          MYSQL_SYNC_TABLES、MYSQL_SYNC_ALL_TABLES
          MYSQL_SYNC_BATCH_SIZE、MYSQL_SYNC_PARALLELISM、MYSQL_SYNC_VERBOSE

        重复数据由 INSERT IGNORE 跳过（依赖目标表的 PRIMARY KEY/UNIQUE KEY）。
        """);

    private static string Required(Dictionary<string, string> values, string key, string environmentKey) =>
        (values.TryGetValue(key, out var value) || TryGetEnvironment(environmentKey, out value)) &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"缺少必填参数: --{key}");

    private static int PositiveInt(Dictionary<string, string> values, string key, string environmentKey, int fallback) =>
        !(values.TryGetValue(key, out var value) || TryGetEnvironment(environmentKey, out value)) ? fallback :
        int.TryParse(value, out var result) && result > 0 ? result :
        throw new ArgumentException($"--{key}/{environmentKey} 必须是正整数");

    private static bool EnvironmentFlag(Dictionary<string, string> values, string key, string environmentKey) =>
        !values.ContainsKey(key) && TryGetEnvironment(environmentKey, out var value) &&
        bool.TryParse(value, out var result) && result;

    private static bool TryGetEnvironment(string key, out string value)
    {
        value = Environment.GetEnvironmentVariable(key) ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }
}

sealed class MySqlSynchronizer(SyncOptions options)
{
    public async Task RunAsync()
    {
        await using var source = new MySqlConnection(options.SourceConnectionString);
        await source.OpenAsync();
        var tables = options.AllTables ? await GetTablesAsync(source) : options.Tables;
        if (tables.Count == 0)
        {
            Console.WriteLine("没有找到需要复制的表。");
            return;
        }

        Console.WriteLine($"开始复制 {tables.Count} 张表，批大小 {options.BatchSize}，并行度 {Math.Min(options.Parallelism, tables.Count)}。 ");
        await Parallel.ForEachAsync(tables,
            new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism },
            async (table, cancellationToken) => await CopyTableAsync(table, cancellationToken));
        Console.WriteLine("全部复制完成。");
    }

    private async Task<IReadOnlyList<string>> GetTablesAsync(MySqlConnection connection)
    {
        await using var command = new MySqlCommand(
            "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME;",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));
        return tables;
    }

    private async Task CopyTableAsync(string table, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        await using var source = new MySqlConnection(options.SourceConnectionString);
        await using var destination = new MySqlConnection(options.DestinationConnectionString);
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);

        var columns = await GetColumnsAsync(source, table, cancellationToken);
        if (columns.Count == 0)
            throw new InvalidOperationException($"源表不存在或没有可复制列: {table}");

        var columnSql = string.Join(",", columns.Select(QuoteIdentifier));
        // MySQL prepared statements have a 65,535-parameter limit per command.
        var effectiveBatchSize = Math.Min(options.BatchSize, Math.Max(1, 65_000 / columns.Count));
        await using var select = new MySqlCommand($"SELECT {columnSql} FROM {QuoteIdentifier(table)};", source);
        select.CommandTimeout = 0;
        await using var reader = await select.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken);
        var total = 0L;
        var batch = new List<object[]>(effectiveBatchSize);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new object[columns.Count];
            reader.GetValues(values);
            batch.Add(values);
            if (batch.Count >= effectiveBatchSize)
            {
                await InsertBatchAsync(destination, table, columns, batch, cancellationToken);
                total += batch.Count;
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            await InsertBatchAsync(destination, table, columns, batch, cancellationToken);
            total += batch.Count;
        }
        Console.WriteLine($"[{table}] 已处理 {total:N0} 行，耗时 {timer.Elapsed}。重复行已跳过。");
    }

    private static async Task<IReadOnlyList<string>> GetColumnsAsync(
        MySqlConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand("""
            SELECT COLUMN_NAME
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table
              AND EXTRA NOT LIKE '%VIRTUAL GENERATED%'
              AND EXTRA NOT LIKE '%STORED GENERATED%'
            ORDER BY ORDINAL_POSITION;
            """, connection);
        command.Parameters.AddWithValue("@table", table);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(0));
        return columns;
    }

    private static async Task InsertBatchAsync(
        MySqlConnection connection, string table, IReadOnlyList<string> columns,
        IReadOnlyList<object[]> rows, CancellationToken cancellationToken)
    {
        var sql = new StringBuilder("INSERT IGNORE INTO ")
            .Append(QuoteIdentifier(table)).Append(" (")
            .Append(string.Join(",", columns.Select(QuoteIdentifier))).Append(") VALUES ");
        await using var command = new MySqlCommand();
        command.Connection = connection;
        command.CommandTimeout = 0;
        for (var row = 0; row < rows.Count; row++)
        {
            if (row > 0) sql.Append(',');
            sql.Append('(');
            for (var column = 0; column < columns.Count; column++)
            {
                if (column > 0) sql.Append(',');
                var parameterName = $"@p{row}_{column}";
                sql.Append(parameterName);
                command.Parameters.AddWithValue(parameterName, rows[row][column]);
            }
            sql.Append(')');
        }
        command.CommandText = sql.ToString();
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string QuoteIdentifier(string value) => $"`{value.Replace("`", "``", StringComparison.Ordinal)}`";
}
