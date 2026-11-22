-- ============================================================
-- DAEMONGUARD DATABASE SCHEMA (v2)
-- ============================================================
-- Changes from v1:
-- - processes uses a surrogate key; identity is (pid, start_time_utc) because Windows reuses PIDs
-- - owner column added (safety step 2: verify owner + executable path)
-- - protected_processes: rule table (name + path pattern) so protection is not PID-based
-- - user_actions keeps its own snapshot and survives sample purging (ON DELETE SET NULL)
-- - estimated savings columns on user_actions (GreenOps: CPU-minutes / memory-MB-hours saved)
-- - daily_process_summary keeps long-term trends after raw samples are purged
-- - evaluation_runs stores ground truth + detector output for the evaluation chapter
-- - all timestamps are UTC (CURRENT_TIMESTAMP is UTC in SQLite)

PRAGMA foreign_keys = ON;

-- ============================================================
-- TABLES
-- ============================================================

CREATE TABLE IF NOT EXISTS processes (
    process_id      INTEGER PRIMARY KEY AUTOINCREMENT,
    pid             INTEGER NOT NULL,
    start_time_utc  DATETIME NOT NULL,
    name            TEXT NOT NULL,
    executable_path TEXT,
    owner           TEXT,
    first_seen      DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_seen       DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    is_protected    INTEGER NOT NULL DEFAULT 0 CHECK(is_protected IN (0, 1)),
    UNIQUE (pid, start_time_utc)
);

-- Read-only (from the app's point of view) protected-process rules.
-- path_pattern is a SQL LIKE pattern matched case-insensitively; NULL = match by name only.
CREATE TABLE IF NOT EXISTS protected_processes (
    rule_id      INTEGER PRIMARY KEY AUTOINCREMENT,
    name         TEXT NOT NULL COLLATE NOCASE,
    path_pattern TEXT
);
-- UNIQUE(name, path_pattern) would not stop duplicate NULL-pattern rows (NULLs are distinct in SQLite),
-- so uniqueness is enforced with an expression index instead.
CREATE UNIQUE INDEX IF NOT EXISTS ux_protected_processes_rule
    ON protected_processes(name, COALESCE(path_pattern, ''));

CREATE TABLE IF NOT EXISTS resource_samples (
    sample_id           INTEGER PRIMARY KEY AUTOINCREMENT,
    process_id          INTEGER NOT NULL,
    timestamp           DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    cpu_percent         REAL,
    memory_mb           REAL,
    disk_io_read_bytes  INTEGER,
    disk_io_write_bytes INTEGER,
    network_in_bytes    INTEGER,
    network_out_bytes   INTEGER,
    gpu_percent         REAL,
    idle_minutes        REAL,
    FOREIGN KEY (process_id) REFERENCES processes(process_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS waste_scores (
    score_id         INTEGER PRIMARY KEY AUTOINCREMENT,
    sample_id        INTEGER NOT NULL UNIQUE,
    waste_score      REAL NOT NULL CHECK(waste_score >= 0 AND waste_score <= 10),
    risk_score       REAL NOT NULL CHECK(risk_score >= 0 AND risk_score <= 10),
    classification   TEXT NOT NULL CHECK(classification IN ('Healthy', 'Suspicious', 'Potentially Wasteful')),
    explanation_text TEXT,
    breakdown_json   TEXT,
    FOREIGN KEY (sample_id) REFERENCES resource_samples(sample_id) ON DELETE CASCADE
);

-- Logged user mitigation actions. Self-contained snapshot so the audit log
-- survives when old samples/scores are purged (score_id becomes NULL).
CREATE TABLE IF NOT EXISTS user_actions (
    action_id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    score_id                   INTEGER,
    pid                        INTEGER,
    process_name               TEXT NOT NULL,
    executable_path            TEXT,
    waste_score_snapshot       REAL,
    risk_score_snapshot        REAL,
    action_type                TEXT NOT NULL CHECK(action_type IN ('Ignore', 'Optimize', 'Restart', 'Close')),
    confirmed_at               DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    result_status              TEXT,
    notes                      TEXT,
    est_cpu_minutes_saved      REAL,
    est_memory_mb_hours_saved  REAL,
    FOREIGN KEY (score_id) REFERENCES waste_scores(score_id) ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS settings (
    setting_key   TEXT PRIMARY KEY,
    setting_value TEXT NOT NULL,
    updated_at    DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Long-term trend data (written by the retention job BEFORE raw samples are deleted)
CREATE TABLE IF NOT EXISTS daily_process_summary (
    day               TEXT NOT NULL,          -- 'YYYY-MM-DD' (UTC)
    process_name      TEXT NOT NULL,
    sample_count      INTEGER NOT NULL,
    avg_cpu_percent   REAL,
    max_cpu_percent   REAL,
    avg_memory_mb     REAL,
    max_waste_score   REAL,
    wasteful_samples  INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (day, process_name)
);

-- Evaluation data: one row per (test scenario, detector, process)
CREATE TABLE IF NOT EXISTS evaluation_runs (
    run_id                   INTEGER PRIMARY KEY AUTOINCREMENT,
    scenario                 TEXT NOT NULL,
    detector                 TEXT NOT NULL CHECK(detector IN ('DaemonGuard', 'StaticThreshold')),
    process_name             TEXT NOT NULL,
    ground_truth             TEXT NOT NULL CHECK(ground_truth IN ('Wasteful', 'Legitimate', 'Protected')),
    predicted_label          TEXT NOT NULL,
    waste_score              REAL,
    detection_time_ms        REAL,
    monitor_cpu_percent      REAL,   -- DaemonGuard's own overhead at the time
    monitor_memory_mb        REAL,
    recorded_at              DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- ============================================================
-- INDEXES
-- ============================================================

CREATE INDEX IF NOT EXISTS idx_resource_samples_process_timestamp ON resource_samples(process_id, timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_resource_samples_timestamp         ON resource_samples(timestamp DESC);
CREATE INDEX IF NOT EXISTS idx_waste_scores_classification        ON waste_scores(classification);
CREATE INDEX IF NOT EXISTS idx_processes_pid                      ON processes(pid);
CREATE INDEX IF NOT EXISTS idx_processes_last_seen                ON processes(last_seen);
CREATE INDEX IF NOT EXISTS idx_user_actions_confirmed_at          ON user_actions(confirmed_at);
CREATE INDEX IF NOT EXISTS idx_evaluation_runs_scenario           ON evaluation_runs(scenario, detector);

-- ============================================================
-- VIEWS
-- ============================================================

-- Latest sample per process (dashboard view)
CREATE VIEW IF NOT EXISTS v_latest_per_process AS
SELECT
    p.process_id,
    p.pid,
    p.name,
    p.executable_path,
    p.owner,
    p.is_protected,
    rs.sample_id,
    rs.cpu_percent,
    rs.memory_mb,
    rs.timestamp AS last_sample_time,
    ws.waste_score,
    ws.risk_score,
    ws.classification,
    ws.explanation_text,
    ws.breakdown_json
FROM processes p
INNER JOIN (
    SELECT process_id, MAX(sample_id) AS max_sample_id
    FROM resource_samples
    GROUP BY process_id
) latest ON p.process_id = latest.process_id
INNER JOIN resource_samples rs ON rs.sample_id = latest.max_sample_id
LEFT JOIN waste_scores ws ON ws.sample_id = rs.sample_id;

-- ============================================================
-- SEED DATA
-- ============================================================

INSERT OR IGNORE INTO settings (setting_key, setting_value) VALUES
('sampling_interval_seconds', '3'),
('retention_days_samples', '7'),
('retention_days_actions', '30'),
('waste_threshold_healthy', '3'),
('waste_threshold_suspicious', '6'),
('max_batch_size', '50');

-- Core Windows processes. Extend this list as needed; the app never writes to this table.
INSERT OR IGNORE INTO protected_processes (name, path_pattern) VALUES
('System', NULL),
('Registry', NULL),
('Memory Compression', NULL),
('System Idle Process', NULL),
('smss.exe',          '%\Windows\System32\%'),
('csrss.exe',         '%\Windows\System32\%'),
('wininit.exe',       '%\Windows\System32\%'),
('winlogon.exe',      '%\Windows\System32\%'),
('services.exe',      '%\Windows\System32\%'),
('lsass.exe',         '%\Windows\System32\%'),
('svchost.exe',       '%\Windows\System32\%'),
('fontdrvhost.exe',   '%\Windows\System32\%'),
('dwm.exe',           '%\Windows\System32\%'),
('spoolsv.exe',       '%\Windows\System32\%'),
('explorer.exe',      '%\Windows\%'),
('MsMpEng.exe',       '%\Windows Defender\%');

-- Retention is handled by DatabaseRepository.RunRetentionCleanupAsync().
