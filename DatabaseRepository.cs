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
/// Uses SemaphoreSlim for async-safe buffering instead of lock+await.
/// </summary>
public sealed class DatabaseRepository : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly List<ResourceSample> _sampleBuffer = new();
    private readonly SemaphoreSlim _bufferLock = new(1, 1);
    private const int MaxBufferSize = 50;
    private bool _disposed;

    /// <summary>
    /// Initializes database connection, enables WAL mode, runs migrations.
    /// Does NOT read settings asynchronously (avoids deadlock on constructor).
    /// </summary>
    public DatabaseRepository(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("Database path required", nameof(dbPath));

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Filename={dbPath};Mode=ReadWriteCreate;");
        _connection.Open();

        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        _connection.Execute("PRAGMA journal_mode=WAL;");
        _connection.Execute("PRAGMA synchronous=NORMAL;");
        _connection.Execute("PRAGMA cache_size=-64000;");

        var schemaPath = Path.Combine(AppContext.BaseDirectory, "schema.sql");
        if (File.Exists(schemaPath))
        {
            var schema = File.ReadAllText(schemaPath);
            _connection.Execute(schema);
        }
        else
        {
            _connection.Execute(GetInlineSchema());
        }
    }

    #region Process Operations

    public async Task<int> UpsertProcessAsync(int pid, string name, string? executablePath, bool isProtected)
    {
        const string sql = @"
            INSERT INTO processes (pid, name, executable_path, is_protected, last_seen)
            VALUES (@Pid, @Name, @ExecutablePath, @IsProtected, CURRENT_TIMESTAMP)
            ON CONFLICT(pid) DO UPDATE SET
                last_seen = CURRENT_TIMESTAMP,
                name = COALESCE(excluded.name, processes.name),
                executable_path = COALESCE(excluded.executable_path, processes.executable_path),
                is_protected = COALESCE(excluded.is_protected, processes.is_protected);";

        var param = new
        {
            Pid = pid,
            Name = name ?? "Unknown",
            ExecutablePath = executablePath,
            IsProtected = isProtected ? 1 : 0
        };

        return await _connection.ExecuteAsync(sql, param);
    }

    public async Task<IEnumerable<int>> GetProtectedPidsAsync()
    {
        const string sql = "SELECT pid FROM processes WHERE is_protected = 1;";
        return await _connection.QueryAsync<int>(sql);
    }

    public async Task<bool> IsProcessProtectedAsync(int pid)
    {
        const string sql = "SELECT COUNT(*) FROM processes WHERE pid = @Pid AND is_protected = 1;";
        var count = await _connection.ExecuteScalarAsync<int>(sql, new { Pid = pid });
        return count > 0;
    }

    #endregion

    #region Resource Sample Operations

    public async Task FlushSampleAsync(ResourceSample sample)
    {
        await _bufferLock.WaitAsync();
        try
        {
            _sampleBuffer.Add(sample);
            
            if (_sampleBuffer.Count >= MaxBufferSize)
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

        const string sql = @"
            INSERT INTO resource_samples 
                (pid, timestamp, cpu_percent, memory_mb, disk_io_read_bytes, disk_io_write_bytes, 
                 network_in_bytes, network_out_bytes, gpu_percent, idle_minutes)
            VALUES 
                (@Pid, @Timestamp, @CpuPercent, @MemoryMb, @DiskIoReadBytes, @DiskIoWriteBytes,
                 @NetworkInBytes, @NetworkOutBytes, @GpuPercent, @IdleMinutes);";

        try
        {
            var samplesCopy = new List<ResourceSample>(_sampleBuffer);
            _sampleBuffer.Clear();
            await _connection.ExecuteAsync(sql, samplesCopy);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to flush samples: {ex.Message}");
        }
    }

    public async Task<ResourceSample?> GetLatestSampleByPidAsync(int pid)
    {
        const string sql = @"
            SELECT * FROM resource_samples 
            WHERE pid = @Pid 
            ORDER BY timestamp DESC LIMIT 1;";

        return await _connection.QueryFirstOrDefaultAsync<ResourceSample>(sql, new { Pid = pid });
    }

    public async Task<IEnumerable<ResourceSample>> GetSamplesByTimeRangeAsync(
        int pid, DateTime startDate, DateTime endDate)
    {
        const string sql = @"
            SELECT * FROM resource_samples
            WHERE pid = @Pid AND timestamp BETWEEN @Start AND @End
            ORDER BY timestamp ASC;";

        return await _connection.QueryAsync<ResourceSample>(sql, new { Pid = pid, Start = startDate, End = endDate });
    }

    #endregion

    #region Waste Score Operations

    public async Task InsertWasteScoreAsync(WasteScore score)
    {
        const string sql = @"
            INSERT INTO waste_scores 
                (sample_id, waste_score, risk_score, classification, explanation_text, breakdown_json)
            VALUES 
                (@SampleId, @WasteScore, @RiskScore, @Classification, @ExplanationText, @BreakdownJson);";

        await _connection.ExecuteAsync(sql, score);
    }

    public async Task<WasteScoreWithSample?> GetWasteScoreForSampleAsync(int sampleId)
    {
        const string sql = @"
            SELECT ws.*, 
                   rs.timestamp as SampleTimestamp, 
                   rs.cpu_percent, rs.memory_mb, rs.pid
            FROM waste_scores ws
            INNER JOIN resource_samples rs ON ws.sample_id = rs.sample_id
            WHERE rs.sample_id = @SampleId;";

        return await _connection.QueryFirstOrDefaultAsync<WasteScoreWithSample>(sql, new { SampleId = sampleId });
    }

    public async Task<IEnumerable<WasteScoreWithSample>> GetRecentWasteScoresAsync(int limit = 100)
    {
        const string sql = @"
            SELECT ws.*, 
                   rs.timestamp as SampleTimestamp, 
                   rs.cpu_percent, rs.memory_mb, rs.pid
            FROM waste_scores ws
            INNER JOIN resource_samples rs ON ws.sample_id = rs.sample_id
            ORDER BY rs.timestamp DESC
            LIMIT @Limit;";

        return await _connection.QueryAsync<WasteScoreWithSample>(sql, new { Limit = limit });
    }

    #endregion

    #region User Action Operations

    public async Task InsertUserActionAsync(int scoreId, string actionType, string? resultStatus, string? notes = null)
    {
        const string sql = @"
            INSERT INTO user_actions (score_id, action_type, result_status, notes)
            VALUES (@ScoreId, @ActionType, @ResultStatus, @Notes);";

        await _connection.ExecuteAsync(sql, new { ScoreId = scoreId, ActionType = actionType, ResultStatus = resultStatus, Notes = notes });
    }

    public async Task<ActionCountDto> GetActionCountsAsync()
    {
        const string sql = @"
            SELECT action_type, COUNT(*) as count
            FROM user_actions
            GROUP BY action_type;";

        var results = await _connection.QueryAsync<(string action_type, int count)>(sql);
        
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
        return await _connection.ExecuteScalarAsync<string?>(sql, new { Key = key });
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

        await _connection.ExecuteAsync(sql, new { Key = key, Value = value });
    }

    #endregion

    #region Maintenance Operations

    public async Task<int> RunRetentionCleanupAsync()
    {
        int deletedSamples = 0;
        int deletedActions = 0;
        int deletedProcesses = 0;

        var retentionDays = await GetSettingIntAsync("retention_days_samples", 7);
        var actionRetention = await GetSettingIntAsync("retention_days_actions", 30);

        deletedSamples = await _connection.ExecuteAsync(@"
            DELETE FROM resource_samples 
            WHERE timestamp < datetime('now', '-' || ? || ' days');", retentionDays);

        deletedActions = await _connection.ExecuteAsync(@"
            DELETE FROM user_actions 
            WHERE confirmed_at < datetime('now', '-' || ? || ' days');", actionRetention);

        deletedProcesses = await _connection.ExecuteAsync(@"
            DELETE FROM processes 
            WHERE last_seen < datetime('now', '-7 days') AND is_protected = 0;");

        await _connection.ExecuteAsync("VACUUM;");

        return deletedSamples + deletedActions + deletedProcesses;
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (_disposed) return;
        
        // Force synchronous flush to avoid fire-and-forget
        ForceFlushAsync().GetAwaiter().GetResult();
        
        _connection.Dispose();
        _bufferLock.Dispose();
        _disposed = true;
    }

    #endregion

    #region Helper Methods

    private static string GetInlineSchema() => @"
        PRAGMA foreign_keys = ON;
        CREATE TABLE IF NOT EXISTS processes (pid INTEGER PRIMARY KEY, name TEXT NOT NULL, executable_path TEXT, first_seen DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, last_seen DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, is_protected INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS resource_samples (sample_id INTEGER PRIMARY KEY AUTOINCREMENT, pid INTEGER NOT NULL, timestamp DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, cpu_percent REAL, memory_mb REAL, disk_io_read_bytes INTEGER, disk_io_write_bytes INTEGER, network_in_bytes INTEGER, network_out_bytes INTEGER, gpu_percent REAL, idle_minutes REAL, FOREIGN KEY (pid) REFERENCES processes(pid) ON DELETE CASCADE);
        CREATE TABLE IF NOT EXISTS waste_scores (score_id INTEGER PRIMARY KEY AUTOINCREMENT, sample_id INTEGER NOT NULL UNIQUE, waste_score REAL NOT NULL, risk_score REAL NOT NULL, classification TEXT NOT NULL, explanation_text TEXT, breakdown_json TEXT, FOREIGN KEY (sample_id) REFERENCES resource_samples(sample_id) ON DELETE CASCADE);
        CREATE TABLE IF NOT EXISTS user_actions (action_id INTEGER PRIMARY KEY AUTOINCREMENT, score_id INTEGER NOT NULL, action_type TEXT NOT NULL, confirmed_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP, result_status TEXT, notes TEXT, FOREIGN KEY (score_id) REFERENCES waste_scores(score_id) ON DELETE CASCADE);
        CREATE TABLE IF NOT EXISTS settings (setting_key TEXT PRIMARY KEY, setting_value TEXT NOT NULL, updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP);
        CREATE INDEX IF NOT EXISTS idx_resource_samples_pid_timestamp ON resource_samples(pid, timestamp DESC);
        INSERT OR IGNORE INTO settings (setting_key, setting_value) VALUES ('sampling_interval_seconds', '3'), ('retention_days_samples', '7'), ('retention_days_actions', '30');
    ";

    #endregion
}

// ============================================================
// DATA TRANSFER OBJECTS (POCOs)
// ============================================================

public class ResourceSample
{
    public int SampleId { get; set; }
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

public class WasteScore
{
    public int ScoreId { get; set; }
    public int SampleId { get; set; }
    public double WasteScore { get; set; }
    public double RiskScore { get; set; }
    public string Classification { get; set; } = string.Empty;
    public string? ExplanationText { get; set; }
    public string? BreakdownJson { get; set; }
}

public class WasteScoreWithSample
{
    public int ScoreId { get; set; }
    public int SampleId { get; set; }
    public double WasteScore { get; set; }
    public double RiskScore { get; set; }
    public string Classification { get; set; } = string.Empty;
    public string? ExplanationText { get; set; }
    public DateTime SampleTimestamp { get; set; }
    public double? CpuPercent { get; set; }
    public double? MemoryMb { get; set; }
    public int Pid { get; set; }
}

public class ActionCountDto
{
    public int Ignore { get; set; }
    public int Optimize { get; set; }
    public int Restart { get; set; }
    public int Close { get; set; }
    
    public int Total => Ignore + Optimize + Restart + Close;
}