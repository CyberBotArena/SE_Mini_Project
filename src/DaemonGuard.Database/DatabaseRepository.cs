using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace DaemonGuard.Database;

/// <summary>
/// Data access layer using Dapper + SQLite.
/// Each operation opens its own connection (pooled, WAL-safe).
/// Buffer uses transactions with rollback on failure.
/// </summary>
public sealed class DatabaseRepository : IAsyncDisposable
{
    private readonly string _connectionString;
    private readonly List<ResourceSampleRecord> _sampleBuffer = new();
    private readonly SemaphoreSlim _bufferLock = new(1, 1);
    private readonly int _maxBatchSize;
    private bool _disposed;

    public DatabaseRepository(string dbPath, int maxBatchSize = 50)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("Database path required", nameof(dbPath));

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Foreign Keys=ON is per-connection, must be in connection string
        _connectionString = $"Filename={dbPath};Mode=ReadWriteCreate;Foreign Keys=True;";
        _maxBatchSize = maxBatchSize;

        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        // Enable WAL mode for concurrent reads/writes
        conn.Execute("PRAGMA journal_mode=WAL;");
        conn.Execute("PRAGMA synchronous=NORMAL;");
        conn.Execute("PRAGMA cache_size=-64000;");

        // CRITICAL: Tell Dapper to match snake_case columns to PascalCase properties
        Dapper.DefaultTypeMap.MatchNamesWithUnderscores = true;

        var schemaPath = Path.Combine(AppContext.BaseDirectory, "schema.sql");
        if (File.Exists(schemaPath))
        {
            var schema = File.ReadAllText(schemaPath);
            conn.Execute(schema);
        }
        else
        {
            throw new InvalidOperationException(
                "schema.sql not found in output directory. Set CopyToOutputDirectory=PreserveNewest.");
        }
    }

    #region Connection Helper

    /// <summary>
    /// Opens a fresh connection for each operation. Connections are pooled,
    /// so this is cheap. Foreign Keys are enforced per-connection.
    /// </summary>
    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }

    #endregion

    #region Process Operations

    public async Task<long> UpsertProcessAsync(
        int pid, string name, string? executablePath, string? owner, bool isCritical)
    {
        const string upsertSql = @"
            INSERT INTO processes (pid, start_time, name, executable_path, owner, is_critical, created_at)
            VALUES (@Pid, @StartTime, @Name, @ExecutablePath, @Owner, @IsCritical, @CreatedAt)
            ON CONFLICT(pid, start_time) DO UPDATE SET
                name = COALESCE(excluded.name, processes.name),
                executable_path = COALESCE(excluded.executable_path, processes.executable_path),
                owner = COALESCE(excluded.owner, processes.owner),
                is_critical = MAX(processes.is_critical, excluded.is_critical);";

        var param = new
        {
            Pid = pid,
            StartTime = DateTime.UtcNow,
            Name = name ?? "Unknown",
            ExecutablePath = executablePath,
            Owner = owner,
            IsCritical = isCritical ? 1 : 0,
            CreatedAt = DateTime.UtcNow
        };

        using var conn = CreateConnection();
        await conn.OpenAsync();

        // First try to find existing process
        var existingId = await conn.ExecuteScalarAsync<long?>(
            "SELECT id FROM processes WHERE pid = @Pid ORDER BY start_time DESC LIMIT 1;",
            new { Pid = pid });

        if (existingId.HasValue)
        {
            await conn.ExecuteAsync(upsertSql, param);
            return existingId.Value;
        }

        // Insert new, return the generated ID
        const string insertSql = @"
            INSERT INTO processes (pid, start_time, name, executable_path, owner, is_critical, created_at)
            VALUES (@Pid, @StartTime, @Name, @ExecutablePath, @Owner, @IsCritical, @CreatedAt);
            SELECT last_insert_rowid();";

        return await conn.ExecuteScalarAsync<long>(insertSql, param);
    }

    public async Task<bool> IsProcessProtectedAsync(int pid, string? name, string? path)
    {
        using var conn = CreateConnection();
        await conn.OpenAsync();

        // Check by pattern matching against protected_processes table
        const string sql = @"
            SELECT COUNT(*) FROM protected_processes pp
            LEFT JOIN processes p ON p.pid = @Pid
            WHERE (pp.name_pattern IS NULL OR p.name LIKE '%' || pp.name_pattern || '%')
              AND (pp.path_pattern IS NULL OR p.executable_path LIKE '%' || pp.path_pattern || '%')
              AND (pp.owner_pattern IS NULL OR p.owner LIKE '%' || pp.owner_pattern || '%');";

        var count = await conn.ExecuteScalarAsync<int>(sql, new { Pid = pid });
        return count > 0;
    }

    public async Task<IEnumerable<ProtectedProcessEntry>> GetProtectedPatternsAsync()
    {
        const string sql = @"
            SELECT name_pattern, path_pattern, owner_pattern, reason
            FROM protected_processes;";

        using var conn = CreateConnection();
        await conn.OpenAsync();
        return await conn.QueryAsync<ProtectedProcessEntry>(sql);
    }

    #endregion

    #region Resource Sample Operations (BUFFERED)

    public async Task FlushSampleAsync(ResourceSampleRecord sample)
    {
        await _bufferLock.WaitAsync();
        try
        {
            _sampleBuffer.Add(sample);
            
            if (_sampleBuffer.Count >= _maxBatchSize)
            {
                await FlushBufferInternalAsync();
            }
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
            if (_sampleBuffer.Count == 0) return;
            await FlushBufferInternalAsync();
        }
        finally
        {
            _bufferLock.Release();
        }
    }

    private async Task FlushBufferInternalAsync()
    {
        if (_sampleBuffer.Count == 0) return;

        using var conn = CreateConnection();
        await conn.OpenAsync();

        using var tx = await conn.BeginTransactionAsync();

        try
        {
            const string sampleInsertSql = @"
                INSERT INTO resource_samples 
                    (process_id, pid, timestamp, cpu_percent, memory_mb, disk_io_read_bytes, 
                     disk_io_write_bytes, network_in_bytes, network_out_bytes, gpu_percent, idle_minutes)
                VALUES 
                    (@ProcessId, @Pid, @Timestamp, @CpuPercent, @MemoryMb, @DiskIoReadBytes, 
                     @DiskIoWriteBytes, @NetworkInBytes, @NetworkOutBytes, @GpuPercent, @IdleMinutes);";

            // Batch insert all samples
            await conn.ExecuteAsync(sampleInsertSql, _sampleBuffer, transaction: tx);
            
            // SUCCESS: only now clear the buffer
            _sampleBuffer.Clear();
            
            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            // Re-throw so caller knows the buffer wasn't cleared
            throw new DbException("Failed to flush sample batch", ex);
        }
    }

    public async Task<ResourceSampleRecord?> GetLatestSampleByPidAsync(int pid)
    {
        const string sql = @"
            SELECT * FROM resource_samples 
            WHERE pid = @Pid 
            ORDER BY timestamp DESC LIMIT 1;";

        using var conn = CreateConnection();
        await conn.OpenAsync();
        return await conn.QueryFirstOrDefaultAsync<ResourceSampleRecord>(sql, new { Pid = pid });
    }

    public async Task<IEnumerable<ResourceSampleRecord>> GetSamplesByTimeRangeAsync(
        int pid, DateTime startDate, DateTime endDate)
    {
        const string sql = @"
            SELECT * FROM resource_samples
            WHERE pid = @Pid AND timestamp BETWEEN @Start AND @End
            ORDER BY timestamp ASC;";

        using var conn = CreateConnection();
        await conn.OpenAsync();
        return await conn.QueryAsync<ResourceSampleRecord>(sql, new { Pid = pid, Start = startDate, End = endDate });
    }

    #endregion

    #region Waste Score Operations

    public async Task<int> InsertWasteScoreAsync(WasteScoreRecord score)
    {
        const string sql = @"
            INSERT INTO waste_scores 
                (sample_id, waste_score, risk_score, classification, explanation_text, breakdown_json)
            VALUES 
                (@SampleId, @WasteScore, @RiskScore, @Classification, @ExplanationText, @BreakdownJson);
            SELECT last_insert_rowid();";

        using var conn = CreateConnection();
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<int>(sql, score);
    }

    public async Task<WasteScoreRecordWithSample?> GetWasteScoreForSampleAsync(int sampleId)
    {
        const string sql = @"
            SELECT ws.*, 
                   rs.timestamp as SampleTimestamp, 
                   rs.cpu_percent, rs.memory_mb, rs.pid,
                   rs.disk_io_read_bytes, rs.disk_io_write_bytes
            FROM waste_scores ws
            INNER JOIN resource_samples rs ON ws.sample_id = rs.sample_id
            WHERE rs.sample_id = @SampleId;";

        using var conn = CreateConnection();
        await conn.OpenAsync();
        return await conn.QueryFirstOrDefaultAsync<WasteScoreRecordWithSample>(sql, new { SampleId = sampleId });
    }

    public async Task<IEnumerable<WasteScoreRecordWithSample>> GetRecentWasteScoresAsync(int limit = 100)
    {
        const string sql = @"
            SELECT ws.*, 
                   rs.timestamp as SampleTimestamp, 
                   rs.cpu_percent, rs.memory_mb, rs.pid,
                   rs.disk_io_read_bytes, rs.disk_io_write_bytes,
                   p.name as ProcessName
            FROM waste_scores ws
            INNER JOIN resource_samples rs ON ws.sample_id = rs.sample_id
            INNER JOIN processes p ON rs.process_id = p.id
            ORDER BY rs.timestamp DESC
            LIMIT @Limit;";

        using var conn = CreateConnection();
        await conn.OpenAsync();
        return await conn.QueryAsync<WasteScoreRecordWithSample>(sql, new { Limit = limit });
    }

    #endregion

    #region User Action Operations (stores pid/name directly)

    public async Task InsertUserActionAsync(
        int? scoreId, int pid, string processName, string actionType, 
        string? resultStatus = null, string? notes = null)
    {
        const string sql = @"
            INSERT INTO user_actions 
                (score_id, pid, process_name, action_type, confirmed_at, result_status, notes)
            VALUES 
                (@ScoreId, @Pid, @ProcessName, @ActionType, @ConfirmedAt, @ResultStatus, @Notes);";

        using var conn = CreateConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync(sql, new 
        { 
            ScoreId = scoreId, 
            Pid = pid, 
            ProcessName = processName,
            ActionType = actionType, 
            ConfirmedAt = DateTime.UtcNow,
            ResultStatus = resultStatus, 
            Notes = notes 
        });
    }

    public async Task<ActionCountDto> GetActionCountsAsync()
    {
        const string sql = @"
            SELECT action_type, COUNT(*) as count
            FROM user_actions
            GROUP BY action_type;";

        using var conn = CreateConnection();
        await conn.OpenAsync();
        
        var results = await conn.QueryAsync<(string action_type, int count)>(sql);
        
        var dto = new ActionCountDto();
        foreach (var row in results)
        {
            switch (row.action_type)
            {
                case "Ignore": dto.Ignore = row.count; break;
                case "Optimize": dto.Optimize = row.count; break;
                case "Restart": dto.Restart = row.count; break;
                case "Close": dto.Close = row.count; break;
            }
        }
        return dto;
    }

    #endregion

    #region Settings Operations

    public async Task<string?> GetSettingAsync(string key)
    {
        const string sql = "SELECT setting_value FROM settings WHERE setting_key = @Key;";
        using var conn = CreateConnection();
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<string?>(sql, new { Key = key });
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
            VALUES (@Key, @Value, @UpdatedAt)
            ON CONFLICT(setting_key) DO UPDATE SET
                setting_value = excluded.setting_value,
                updated_at = CURRENT_TIMESTAMP;";

        using var conn = CreateConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync(sql, new { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
    }

    #endregion

    #region Daily Aggregation (for long-term trends)

    public async Task UpsertDailyAggregateAsync(DailyAggregateRecord aggregate)
    {
        const string sql = @"
            INSERT INTO daily_aggregates 
                (pid, date, avg_cpu, max_memory_mb, total_disk_io_bytes, avg_idle_minutes, waste_score_avg)
            VALUES 
                (@Pid, @Date, @AvgCpu, @MaxMemoryMb, @TotalDiskIoBytes, @AvgIdleMinutes, @WasteScoreAvg)
            ON CONFLICT(pid, date) DO UPDATE SET
                avg_cpu = excluded.avg_cpu,
                max_memory_mb = excluded.max_memory_mb,
                total_disk_io_bytes = excluded.total_disk_io_bytes,
                avg_idle_minutes = excluded.avg_idle_minutes,
                waste_score_avg = excluded.waste_score_avg;";

        using var conn = CreateConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync(sql, aggregate);
    }

    public async Task<DailyAggregateRecord?> GetDailyAggregateAsync(int pid, DateTime date)
    {
        const string sql = @"
            SELECT * FROM daily_aggregates 
            WHERE pid = @Pid AND date = @Date;";

        using var conn = CreateConnection();
        await conn.OpenAsync();
        return await conn.QueryFirstOrDefaultAsync<DailyAggregateRecord>(sql, 
            new { Pid = pid, Date = date.Date });
    }

    #endregion

    #region Maintenance Operations

    public async Task<int> RunRetentionCleanupAsync(bool doVacuum = false)
    {
        int deletedSamples = 0;
        int deletedActions = 0;
        int deletedProcesses = 0;

        using var conn = CreateConnection();
        await conn.OpenAsync();

        using var tx = await conn.BeginTransactionAsync();

        try
        {
            var retentionDays = await GetSettingIntAsync("retention_days_samples", 7);
            var actionRetention = await GetSettingIntAsync("retention_days_actions", 30);

            // Delete old samples (with CASCADE, waste_scores will follow)
            deletedSamples = await conn.ExecuteAsync(@"
                DELETE FROM resource_samples 
                WHERE timestamp < datetime('now', '-' || @Days || ' days');",
                new { Days = retentionDays }, transaction: tx);

            // Delete old user actions (NOT cascade-deleted, they're standalone)
            deletedActions = await conn.ExecuteAsync(@"
                DELETE FROM user_actions 
                WHERE confirmed_at < datetime('now', '-' || @Days || ' days');",
                new { Days = actionRetention }, transaction: tx);

            // Clean orphaned processes (no samples in 7 days, not critical)
            deletedProcesses = await conn.ExecuteAsync(@"
                DELETE FROM processes 
                WHERE id NOT IN (
                    SELECT DISTINCT process_id FROM resource_samples 
                    WHERE timestamp > datetime('now', '-7 days')
                )
                AND is_critical = 0;", transaction: tx);

            await tx.CommitAsync();

            // VACUUM is expensive - only run if requested (e.g., weekly)
            if (doVacuum)
            {
                await conn.ExecuteAsync("VACUUM;");
            }

            return deletedSamples + deletedActions + deletedProcesses;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    #endregion

    #region Evaluation Support

    public async Task<long> CreateEvaluationRunAsync(string name, string scenarioType, string? notes = null)
    {
        const string sql = @"
            INSERT INTO evaluation_runs (run_name, started_at, scenario_type, notes)
            VALUES (@Name, @StartedAt, @ScenarioType, @Notes);
            SELECT last_insert_rowid();";

        using var conn = CreateConnection();
        await conn.OpenAsync();
        return await conn.ExecuteScalarAsync<long>(sql, new
        {
            Name = name,
            StartedAt = DateTime.UtcNow,
            ScenarioType = scenarioType,
            Notes = notes
        });
    }

    public async Task InsertEvaluationLabelAsync(
        long runId, int pid, string actualClassification, 
        string? detectedClassification = null, DateTime? detectedAt = null)
    {
        const string sql = @"
            INSERT INTO evaluation_labels 
                (run_id, pid, actual_classification, detected_classification, detected_at)
            VALUES 
                (@RunId, @Pid, @Actual, @Detected, @DetectedAt);";

        using var conn = CreateConnection();
        await conn.OpenAsync();
        await conn.ExecuteAsync(sql, new
        {
            RunId = runId,
            Pid = pid,
            Actual = actualClassification,
            Detected = detectedClassification,
            DetectedAt = detectedAt ?? (object)DBNull.Value
        });
    }

    #endregion

    #region IAsyncDisposable

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        
        await ForceFlushAsync();
        _bufferLock.Dispose();
        _disposed = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        ForceFlushAsync().GetAwaiter().GetResult();
        _bufferLock.Dispose();
        _disposed = true;
    }

    #endregion
}

// ============================================================
// DATA TRANSFER OBJECTS (POCOs)
// ============================================================

public class ResourceSampleRecord
{
    public int SampleId { get; set; }
    public int ProcessId { get; set; }
    public int Pid { get; set; }
    public DateTime Timestamp { get; set; }
    public double? CpuPercent { get; set; }
    public double? MemoryMb { get; set; }
    public long? DiskIoReadBytes { get; set; }
    public long? DiskIoWriteBytes { get; set; }
    public long? NetworkInBytes { get; set; }
    public long? NetworkOutBytes { get; set; }
    public double? GpuPercent { get; set; }
    public double? IdleMinutes { get; set; }
}

public class WasteScoreRecord
{
    public int ScoreId { get; set; }
    public int SampleId { get; set; }
    public double WasteScore { get; set; }  // Renamed from WasteScore property
    public double RiskScore { get; set; }
    public string Classification { get; set; } = string.Empty;
    public string? ExplanationText { get; set; }
    public string? BreakdownJson { get; set; }
}

public class WasteScoreRecordWithSample
{
    public int ScoreId { get; set; }
    public int SampleId { get; set; }
    public double WasteScore { get; set; }
    public double RiskScore { get; set; }
    public string Classification { get; set; } = string.Empty;
    public string? ExplanationText { get; set; }
    public string? BreakdownJson { get; set; }
    public DateTime SampleTimestamp { get; set; }
    public double? CpuPercent { get; set; }
    public double? MemoryMb { get; set; }
    public int Pid { get; set; }
    public long? DiskIoReadBytes { get; set; }
    public long? DiskIoWriteBytes { get; set; }
    public string? ProcessName { get; set; }
}

public class DailyAggregateRecord
{
    public int Id { get; set; }
    public int Pid { get; set; }
    public DateTime Date { get; set; }
    public double? AvgCpu { get; set; }
    public double? MaxMemoryMb { get; set; }
    public long? TotalDiskIoBytes { get; set; }
    public double? AvgIdleMinutes { get; set; }
    public double? WasteScoreAvg { get; set; }
}

public class ProtectedProcessEntry
{
    public string NamePattern { get; set; } = string.Empty;
    public string? PathPattern { get; set; }
    public string? OwnerPattern { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class ActionCountDto
{
    public int Ignore { get; set; }
    public int Optimize { get; set; }
    public int Restart { get; set; }
    public int Close { get; set; }
    
    public int Total => Ignore + Optimize + Restart + Close;
}

public class DbException : Exception
{
    public DbException(string message) : base(message) { }
    public DbException(string message, Exception inner) : base(message, inner) { }
}
