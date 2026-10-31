using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace DaemonGuard.Database;

/// <summary>
/// Data access layer using Dapper + SQLite.
///
/// Design notes:
/// - A short-lived pooled connection is opened per operation (SqliteConnection is not thread-safe).
///   WAL mode lets dashboard reads run while the collector writes.
/// - Samples (and their scores) are buffered and written in ONE transaction. The buffer is only
///   cleared after a successful commit, so a failed flush never loses data.
/// - All timestamps are UTC.
/// - Protection is rule-based (name + path pattern) and can never be downgraded by an upsert.
/// </summary>
public sealed class DatabaseRepository : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly List<BufferedSample> _buffer = new();
    private readonly SemaphoreSlim _bufferLock = new(1, 1);
    private readonly int _maxBufferSize;
    private const int DefaultMaxBufferSize = 50;
    private const int MaxPendingSamples = 2000; // hard cap if the DB keeps failing
    private bool _disposed;

    static DatabaseRepository()
    {
        // Maps cpu_percent -> CpuPercent, process_id -> ProcessId, etc.
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public DatabaseRepository(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("Database path required", nameof(dbPath));

        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,     // per-connection setting, so it must be in the connection string
            DefaultTimeout = 30,
            Pooling = true
        }.ToString();

        _maxBufferSize = InitializeDatabase();
    }

    /// <returns>The configured batch size.</returns>
    private int InitializeDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        conn.Execute("PRAGMA journal_mode=WAL;"); // persistent in the database file

        // Detect a database created by the old (v1) schema.
        var hasProcesses = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM pragma_table_info('processes');") > 0;
        var hasV2 = conn.ExecuteScalar<int>("SELECT COUNT(*) FROM pragma_table_info('processes') WHERE name = 'process_id';") > 0;
        if (hasProcesses && !hasV2)
            throw new InvalidOperationException(
                "Existing database uses the old schema. Delete the .db file (and -wal/-shm) so it is recreated.");

        // schema.sql is the single source of truth. Set it to "Copy to Output Directory" in the .csproj.
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "schema.sql");
        if (!File.Exists(schemaPath))
            throw new FileNotFoundException(
                "schema.sql not found next to the executable. Set its Build Action to Content and " +
                "'Copy to Output Directory' to 'Copy if newer'.", schemaPath);

        conn.Execute(File.ReadAllText(schemaPath));

        var batch = conn.ExecuteScalar<string?>("SELECT setting_value FROM settings WHERE setting_key = 'max_batch_size';");
        return int.TryParse(batch, out var n) && n > 0 ? n : DefaultMaxBufferSize;
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var conn = new SqliteConnection(_connectionString);
        try
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync("PRAGMA synchronous=NORMAL;");
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    #region Process Operations

    /// <summary>
    /// Inserts or updates a process identified by (pid, start time) and returns its process_id.
    /// Protection is the OR of the caller's flag, the stored flag and the rule table — it can never be downgraded.
    /// </summary>
    public async Task<long> UpsertProcessAsync(
        int pid, DateTime startTimeUtc, string? name, string? executablePath, string? owner, bool isProtected)
    {
        var safeName = string.IsNullOrWhiteSpace(name) ? "Unknown" : name;
        var protect = isProtected || await IsProtectedByRuleAsync(safeName, executablePath);

        const string sql = @"
            INSERT INTO processes (pid, start_time_utc, name, executable_path, owner, is_protected, first_seen, last_seen)
            VALUES (@Pid, @StartTimeUtc, @Name, @ExecutablePath, @Owner, @IsProtected, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
            ON CONFLICT(pid, start_time_utc) DO UPDATE SET
                last_seen       = CURRENT_TIMESTAMP,
                name            = excluded.name,
                executable_path = COALESCE(excluded.executable_path, processes.executable_path),
                owner           = COALESCE(excluded.owner, processes.owner),
                is_protected    = MAX(processes.is_protected, excluded.is_protected)
            RETURNING process_id;";

        await using var conn = await OpenAsync();
        return await conn.ExecuteScalarAsync<long>(sql, new
        {
            Pid = pid,
            StartTimeUtc = startTimeUtc,
            Name = safeName,
            ExecutablePath = executablePath,
            Owner = owner,
            IsProtected = protect ? 1 : 0
        });
    }

    /// <summary>Checks the read-only protected_processes rule table (name + optional path pattern).</summary>
    public async Task<bool> IsProtectedByRuleAsync(string name, string? executablePath)
    {
        const string sql = @"
            SELECT COUNT(*) FROM protected_processes
            WHERE name = @Name COLLATE NOCASE
              AND (path_pattern IS NULL
                   OR (@Path IS NOT NULL AND lower(@Path) LIKE lower(path_pattern)));";

        await using var conn = await OpenAsync();
        return await conn.ExecuteScalarAsync<int>(sql, new { Name = name, Path = executablePath }) > 0;
    }

    public async Task<IEnumerable<long>> GetProtectedProcessIdsAsync()
    {
        await using var conn = await OpenAsync();
        return await conn.QueryAsync<long>("SELECT process_id FROM processes WHERE is_protected = 1;");
    }

    public async Task<bool> IsProcessProtectedAsync(long processId)
    {
        await using var conn = await OpenAsync();
        var count = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM processes WHERE process_id = @ProcessId AND is_protected = 1;",
            new { ProcessId = processId });
        return count > 0;
    }

    #endregion

    #region Resource Sample Operations

    /// <summary>
    /// Queues a sample (and optionally its waste score) for batched insertion.
    /// sample.ProcessId must come from UpsertProcessAsync. The score's SampleId is filled in at flush time.
    /// Throws if a triggered flush fails; the queued data is kept and retried on the next flush.
    /// </summary>
    public async Task QueueSampleAsync(ResourceSample sample, WasteScoreRecord? score = null)
    {
        await _bufferLock.WaitAsync();
        try
        {
            _buffer.Add(new BufferedSample(sample, score));

            if (_buffer.Count > MaxPendingSamples)
            {
                var drop = _buffer.Count - MaxPendingSamples;
                _buffer.RemoveRange(0, drop);
                System.Diagnostics.Trace.TraceError($"DaemonGuard DB: dropped {drop} oldest samples (database unavailable).");
            }

            if (_buffer.Count >= _maxBufferSize)
                await FlushBufferInternalAsync();
        }
        finally
        {
            _bufferLock.Release();
        }
    }

    public async Task ForceFlushAsync()
    {
        await _bufferLock.WaitAsync();
        try
        {
            await FlushBufferInternalAsync();
        }
        finally
        {
            _bufferLock.Release();
        }
    }

    // Caller must hold _bufferLock.
    private async Task FlushBufferInternalAsync()
    {
        if (_buffer.Count == 0) return;

        const string sampleSql = @"
            INSERT INTO resource_samples
                (process_id, timestamp, cpu_percent, memory_mb, disk_io_read_bytes, disk_io_write_bytes,
                 network_in_bytes, network_out_bytes, gpu_percent, idle_minutes)
            VALUES
                (@ProcessId, @Timestamp, @CpuPercent, @MemoryMb, @DiskIoReadBytes, @DiskIoWriteBytes,
                 @NetworkInBytes, @NetworkOutBytes, @GpuPercent, @IdleMinutes)
            RETURNING sample_id;";

        await using var conn = await OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Any exception rolls the transaction back (on dispose) and leaves _buffer untouched.
        foreach (var item in _buffer)
        {
            var sampleId = await conn.ExecuteScalarAsync<long>(sampleSql, item.Sample, tx);
            item.Sample.SampleId = sampleId;

            if (item.Score is not null)
            {
                item.Score.SampleId = sampleId;
                await conn.ExecuteAsync(InsertWasteScoreSql, item.Score, tx);
            }
        }

        await tx.CommitAsync();
        _buffer.Clear(); // only after a successful commit
    }

    public async Task<ResourceSample?> GetLatestSampleByProcessIdAsync(long processId)
    {
        const string sql = @"
            SELECT * FROM resource_samples
            WHERE process_id = @ProcessId
            ORDER BY timestamp DESC LIMIT 1;";

        await using var conn = await OpenAsync();
        return await conn.QueryFirstOrDefaultAsync<ResourceSample>(sql, new { ProcessId = processId });
    }

    public async Task<IEnumerable<ResourceSample>> GetSamplesByTimeRangeAsync(
        long processId, DateTime startUtc, DateTime endUtc)
    {
        const string sql = @"
            SELECT * FROM resource_samples
            WHERE process_id = @ProcessId AND timestamp BETWEEN @Start AND @End
            ORDER BY timestamp ASC;";

        await using var conn = await OpenAsync();
        return await conn.QueryAsync<ResourceSample>(sql, new { ProcessId = processId, Start = startUtc, End = endUtc });
    }

    #endregion

    #region Waste Score Operations

    private const string InsertWasteScoreSql = @"
        INSERT INTO waste_scores
            (sample_id, waste_score, risk_score, classification, explanation_text, breakdown_json)
        VALUES
            (@SampleId, @WasteScore, @RiskScore, @Classification, @ExplanationText, @BreakdownJson);";

    /// <summary>For a sample that is already stored. Prefer QueueSampleAsync(sample, score) for new data.</summary>
    public async Task InsertWasteScoreAsync(WasteScoreRecord score)
    {
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(InsertWasteScoreSql, score);
    }

    private const string WasteScoreWithSampleSelect = @"
        SELECT ws.score_id, ws.sample_id, ws.waste_score, ws.risk_score, ws.classification,
               ws.explanation_text, ws.breakdown_json,
               rs.timestamp AS sample_timestamp, rs.cpu_percent, rs.memory_mb,
               p.process_id, p.pid, p.name AS process_name, p.is_protected
        FROM waste_scores ws
        INNER JOIN resource_samples rs ON ws.sample_id = rs.sample_id
        INNER JOIN processes p ON rs.process_id = p.process_id";

    public async Task<WasteScoreWithSample?> GetWasteScoreForSampleAsync(long sampleId)
    {
        const string sql = WasteScoreWithSampleSelect + " WHERE rs.sample_id = @SampleId;";

        await using var conn = await OpenAsync();
        return await conn.QueryFirstOrDefaultAsync<WasteScoreWithSample>(sql, new { SampleId = sampleId });
    }

    public async Task<IEnumerable<WasteScoreWithSample>> GetRecentWasteScoresAsync(int limit = 100)
    {
        const string sql = WasteScoreWithSampleSelect + " ORDER BY rs.timestamp DESC LIMIT @Limit;";

        await using var conn = await OpenAsync();
        return await conn.QueryAsync<WasteScoreWithSample>(sql, new { Limit = limit });
    }

    #endregion

    #region User Action Operations

    public async Task<long> InsertUserActionAsync(UserActionRecord action)
    {
        const string sql = @"
            INSERT INTO user_actions
                (score_id, pid, process_name, executable_path, waste_score_snapshot, risk_score_snapshot,
                 action_type, result_status, notes, est_cpu_minutes_saved, est_memory_mb_hours_saved)
            VALUES
                (@ScoreId, @Pid, @ProcessName, @ExecutablePath, @WasteScoreSnapshot, @RiskScoreSnapshot,
                 @ActionType, @ResultStatus, @Notes, @EstCpuMinutesSaved, @EstMemoryMbHoursSaved)
            RETURNING action_id;";

        await using var conn = await OpenAsync();
        return await conn.ExecuteScalarAsync<long>(sql, action);
    }

    public async Task<ActionCountDto> GetActionCountsAsync()
    {
        const string sql = @"
            SELECT action_type AS ActionType, COUNT(*) AS Count
            FROM user_actions
            GROUP BY action_type;";

        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync<ActionCountRow>(sql);

        var dto = new ActionCountDto();
        foreach (var row in rows)
        {
            switch (row.ActionType)
            {
                case "Ignore": dto.Ignore = row.Count; break;
                case "Optimize": dto.Optimize = row.Count; break;
                case "Restart": dto.Restart = row.Count; break;
                case "Close": dto.Close = row.Count; break;
            }
        }
        return dto;
    }

    /// <summary>Total estimated savings across all logged actions (GreenOps summary).</summary>
    public async Task<SavingsSummary> GetSavingsSummaryAsync()
    {
        const string sql = @"
            SELECT COALESCE(SUM(est_cpu_minutes_saved), 0)     AS CpuMinutesSaved,
                   COALESCE(SUM(est_memory_mb_hours_saved), 0) AS MemoryMbHoursSaved
            FROM user_actions
            WHERE action_type IN ('Optimize', 'Restart', 'Close');";

        await using var conn = await OpenAsync();
        return await conn.QuerySingleAsync<SavingsSummary>(sql);
    }

    #endregion

    #region Settings Operations

    public async Task<string?> GetSettingAsync(string key)
    {
        await using var conn = await OpenAsync();
        return await conn.ExecuteScalarAsync<string?>(
            "SELECT setting_value FROM settings WHERE setting_key = @Key;", new { Key = key });
    }

    public async Task<int> GetSettingIntAsync(string key, int defaultValue = 0)
    {
        var value = await GetSettingAsync(key);
        return int.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    public async Task SetSettingAsync(string key, string value)
    {
        const string sql = @"
            INSERT INTO settings (setting_key, setting_value, updated_at)
            VALUES (@Key, @Value, CURRENT_TIMESTAMP)
            ON CONFLICT(setting_key) DO UPDATE SET
                setting_value = excluded.setting_value,
                updated_at = CURRENT_TIMESTAMP;";

        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(sql, new { Key = key, Value = value });
    }

    #endregion

    #region Trends and Evaluation

    public async Task<IEnumerable<DailyProcessSummary>> GetDailySummariesAsync(string fromDay, string toDay)
    {
        const string sql = @"
            SELECT day, process_name, sample_count, avg_cpu_percent, max_cpu_percent,
                   avg_memory_mb, max_waste_score, wasteful_samples
            FROM daily_process_summary
            WHERE day BETWEEN @FromDay AND @ToDay
            ORDER BY day, process_name;";

        await using var conn = await OpenAsync();
        return await conn.QueryAsync<DailyProcessSummary>(sql, new { FromDay = fromDay, ToDay = toDay });
    }

    public async Task InsertEvaluationRunAsync(EvaluationRun run)
    {
        const string sql = @"
            INSERT INTO evaluation_runs
                (scenario, detector, process_name, ground_truth, predicted_label, waste_score,
                 detection_time_ms, monitor_cpu_percent, monitor_memory_mb)
            VALUES
                (@Scenario, @Detector, @ProcessName, @GroundTruth, @PredictedLabel, @WasteScore,
                 @DetectionTimeMs, @MonitorCpuPercent, @MonitorMemoryMb);";

        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(sql, run);
    }

    public async Task<IEnumerable<EvaluationRun>> GetEvaluationRunsAsync(string? scenario = null)
    {
        const string sql = @"
            SELECT run_id, scenario, detector, process_name, ground_truth, predicted_label, waste_score,
                   detection_time_ms, monitor_cpu_percent, monitor_memory_mb, recorded_at
            FROM evaluation_runs
            WHERE @Scenario IS NULL OR scenario = @Scenario
            ORDER BY run_id;";

        await using var conn = await OpenAsync();
        return await conn.QueryAsync<EvaluationRun>(sql, new { Scenario = scenario });
    }

    #endregion

    #region Maintenance Operations

    /// <summary>
    /// Rolls old samples into daily_process_summary, then deletes expired samples, actions and processes.
    /// Deleting samples cascades to waste_scores; user_actions are kept (score_id becomes NULL)
    /// until their own, longer retention period expires.
    /// Run VACUUM rarely (e.g. weekly): pass vacuum = true.
    /// </summary>
    public async Task<int> RunRetentionCleanupAsync(bool vacuum = false)
    {
        var sampleDays = await GetSettingIntAsync("retention_days_samples", 7);
        var actionDays = await GetSettingIntAsync("retention_days_actions", 30);

        const string aggregateSql = @"
            INSERT INTO daily_process_summary
                (day, process_name, sample_count, avg_cpu_percent, max_cpu_percent,
                 avg_memory_mb, max_waste_score, wasteful_samples)
            SELECT date(rs.timestamp), p.name, COUNT(*), AVG(rs.cpu_percent), MAX(rs.cpu_percent),
                   AVG(rs.memory_mb), MAX(ws.waste_score),
                   SUM(CASE WHEN ws.classification = 'Potentially Wasteful' THEN 1 ELSE 0 END)
            FROM resource_samples rs
            INNER JOIN processes p ON p.process_id = rs.process_id
            LEFT JOIN waste_scores ws ON ws.sample_id = rs.sample_id
            WHERE rs.timestamp < datetime('now', '-' || @Days || ' days')
            GROUP BY date(rs.timestamp), p.name
            ON CONFLICT(day, process_name) DO UPDATE SET
                -- a day can be rolled up in two passes, so merge as weighted averages
                avg_cpu_percent  = (COALESCE(daily_process_summary.avg_cpu_percent, 0) * daily_process_summary.sample_count
                                    + COALESCE(excluded.avg_cpu_percent, 0) * excluded.sample_count)
                                   / (daily_process_summary.sample_count + excluded.sample_count),
                avg_memory_mb    = (COALESCE(daily_process_summary.avg_memory_mb, 0) * daily_process_summary.sample_count
                                    + COALESCE(excluded.avg_memory_mb, 0) * excluded.sample_count)
                                   / (daily_process_summary.sample_count + excluded.sample_count),
                max_cpu_percent  = MAX(COALESCE(daily_process_summary.max_cpu_percent, 0), COALESCE(excluded.max_cpu_percent, 0)),
                max_waste_score  = MAX(COALESCE(daily_process_summary.max_waste_score, 0), COALESCE(excluded.max_waste_score, 0)),
                wasteful_samples = daily_process_summary.wasteful_samples + excluded.wasteful_samples,
                sample_count     = daily_process_summary.sample_count + excluded.sample_count;";

        int total;
        await using (var conn = await OpenAsync())
        await using (var tx = await conn.BeginTransactionAsync())
        {
            await conn.ExecuteAsync(aggregateSql, new { Days = sampleDays }, tx);

            var deletedSamples = await conn.ExecuteAsync(
                "DELETE FROM resource_samples WHERE timestamp < datetime('now', '-' || @Days || ' days');",
                new { Days = sampleDays }, tx);

            var deletedActions = await conn.ExecuteAsync(
                "DELETE FROM user_actions WHERE confirmed_at < datetime('now', '-' || @Days || ' days');",
                new { Days = actionDays }, tx);

            var deletedProcesses = await conn.ExecuteAsync(
                "DELETE FROM processes WHERE last_seen < datetime('now', '-' || @Days || ' days');",
                new { Days = sampleDays }, tx);

            await tx.CommitAsync();
            total = deletedSamples + deletedActions + deletedProcesses;
        }

        if (vacuum)
        {
            await using var conn = await OpenAsync();
            await conn.ExecuteAsync("VACUUM;");
        }

        return total;
    }

    #endregion

    #region IAsyncDisposable

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            await ForceFlushAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"DaemonGuard DB: final flush failed: {ex.Message}");
        }

        _bufferLock.Dispose();
        SqliteConnection.ClearAllPools(); // releases the file so the DB can be moved/deleted
    }

    #endregion

    private sealed record BufferedSample(ResourceSample Sample, WasteScoreRecord? Score);
    private sealed class ActionCountRow { public string ActionType { get; set; } = ""; public int Count { get; set; } }
}

// ============================================================
// DATA TRANSFER OBJECTS (POCOs)
// ============================================================

public class ResourceSample
{
    public long SampleId { get; set; }
    public long ProcessId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow; // always UTC
    public double? CpuPercent { get; set; }
    public double? MemoryMb { get; set; }
    public long? DiskIoReadBytes { get; set; }
    public long? DiskIoWriteBytes { get; set; }
    public long? NetworkInBytes { get; set; }
    public long? NetworkOutBytes { get; set; }
    public double? GpuPercent { get; set; }
    public double? IdleMinutes { get; set; }
}

/// <summary>Renamed from WasteScore: a class cannot contain a member with the same name as itself (CS0542).</summary>
public class WasteScoreRecord
{
    public long ScoreId { get; set; }
    public long SampleId { get; set; }
    public double WasteScore { get; set; }
    public double RiskScore { get; set; }
    public string Classification { get; set; } = string.Empty; // Healthy | Suspicious | Potentially Wasteful
    public string? ExplanationText { get; set; }
    public string? BreakdownJson { get; set; }
}

public class WasteScoreWithSample
{
    public long ScoreId { get; set; }
    public long SampleId { get; set; }
    public double WasteScore { get; set; }
    public double RiskScore { get; set; }
    public string Classification { get; set; } = string.Empty;
    public string? ExplanationText { get; set; }
    public string? BreakdownJson { get; set; }
    public DateTime SampleTimestamp { get; set; }
    public double? CpuPercent { get; set; }
    public double? MemoryMb { get; set; }
    public long ProcessId { get; set; }
    public int Pid { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public bool IsProtected { get; set; }
}

public class UserActionRecord
{
    public long? ScoreId { get; set; }
    public int? Pid { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public string? ExecutablePath { get; set; }
    public double? WasteScoreSnapshot { get; set; }
    public double? RiskScoreSnapshot { get; set; }
    public string ActionType { get; set; } = string.Empty; // Ignore | Optimize | Restart | Close
    public string? ResultStatus { get; set; }
    public string? Notes { get; set; }
    public double? EstCpuMinutesSaved { get; set; }
    public double? EstMemoryMbHoursSaved { get; set; }
}

public class ActionCountDto
{
    public int Ignore { get; set; }
    public int Optimize { get; set; }
    public int Restart { get; set; }
    public int Close { get; set; }

    public int Total => Ignore + Optimize + Restart + Close;
}

public class SavingsSummary
{
    public double CpuMinutesSaved { get; set; }
    public double MemoryMbHoursSaved { get; set; }
}

public class DailyProcessSummary
{
    public string Day { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public int SampleCount { get; set; }
    public double? AvgCpuPercent { get; set; }
    public double? MaxCpuPercent { get; set; }
    public double? AvgMemoryMb { get; set; }
    public double? MaxWasteScore { get; set; }
    public int WastefulSamples { get; set; }
}

public class EvaluationRun
{
    public long RunId { get; set; }
    public string Scenario { get; set; } = string.Empty;
    public string Detector { get; set; } = "DaemonGuard"; // DaemonGuard | StaticThreshold
    public string ProcessName { get; set; } = string.Empty;
    public string GroundTruth { get; set; } = string.Empty; // Wasteful | Legitimate | Protected
    public string PredictedLabel { get; set; } = string.Empty;
    public double? WasteScore { get; set; }
    public double? DetectionTimeMs { get; set; }
    public double? MonitorCpuPercent { get; set; }
    public double? MonitorMemoryMb { get; set; }
    public DateTime RecordedAt { get; set; }
}
