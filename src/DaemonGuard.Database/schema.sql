-- ============================================================
-- DAEMONGUARD DATABASE SCHEMA (FINAL)
-- ============================================================
-- All fixes applied:
-- - Surrogate ID for processes (PID can be reused)
-- - Protected processes stored by name/path/owner (not mutable PID)
-- - User actions store pid/name directly (cascade won't delete)
-- - Proper CHECK constraints, indexes, and UTC timestamps

PRAGMA foreign_keys = ON;

-- ============================================================
-- PROCESSES TABLE (surrogate ID + start_time for uniqueness)
-- ============================================================

CREATE TABLE IF NOT EXISTS processes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    pid INTEGER NOT NULL,
    start_time DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    name TEXT NOT NULL,
    executable_path TEXT,
    owner TEXT,
    is_critical INTEGER NOT NULL DEFAULT 0,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(pid, start_time)
);

-- ============================================================
-- PROTECTED PROCESSES LIST (immutable by design)
-- Critical processes protected by name/path/owner pattern, not PID
-- ============================================================

CREATE TABLE IF NOT EXISTS protected_processes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name_pattern TEXT NOT NULL,
    path_pattern TEXT,
    owner_pattern TEXT,
    reason TEXT,
    created_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Seed critical Windows processes
INSERT OR IGNORE INTO protected_processes (name_pattern, path_pattern, owner_pattern, reason) VALUES
('system', 'System', 'NT AUTHORITY', 'Core Windows kernel process'),
('smss.exe', 'System32\\', 'NT AUTHORITY', 'Session Manager Subsystem'),
('csrss.exe', 'System32\\', 'NT AUTHORITY', 'Client Server Runtime Subsystem'),
('services.exe', 'System32\\', 'NT AUTHORITY', 'Service Control Manager'),
('lsass.exe', 'System32\\', 'NT AUTHORITY', 'Local Security Authority'),
('wininit.exe', 'System32\\', 'NT AUTHORITY', 'Windows Initialization'),
('winlogon.exe', 'System32\\', 'NT AUTHORITY', 'Windows Logon');

-- ============================================================
-- RESOURCE SAMPLES (raw telemetry)
-- ============================================================

CREATE TABLE IF NOT EXISTS resource_samples (
    sample_id INTEGER PRIMARY KEY AUTOINCREMENT,
    process_id INTEGER NOT NULL,
    pid INTEGER NOT NULL,
    timestamp DATETIME NOT NULL,
    cpu_percent REAL,
    memory_mb REAL,
    disk_io_read_bytes INTEGER,
    disk_io_write_bytes INTEGER,
    network_in_bytes INTEGER,
    network_out_bytes INTEGER,
    gpu_percent REAL,
    idle_minutes REAL,
    FOREIGN KEY (process_id) REFERENCES processes(id) ON DELETE CASCADE
);

-- ============================================================
-- WASTE SCORES (calculated heuristics)
-- ============================================================

CREATE TABLE IF NOT EXISTS waste_scores (
    score_id INTEGER PRIMARY KEY AUTOINCREMENT,
    sample_id INTEGER NOT NULL UNIQUE,
    waste_score REAL NOT NULL CHECK(waste_score >= 0 AND waste_score <= 10),
    risk_score REAL NOT NULL CHECK(risk_score >= 0 AND risk_score <= 10),
    classification TEXT NOT NULL CHECK(classification IN ('Healthy', 'Suspicious', 'Potentially Wasteful')),
    explanation_text TEXT,
    breakdown_json TEXT,
    FOREIGN KEY (sample_id) REFERENCES resource_samples(sample_id) ON DELETE CASCADE
);

-- ============================================================
-- USER ACTIONS (stores pid/name directly, not cascade-deleted)
-- ============================================================

CREATE TABLE IF NOT EXISTS user_actions (
    action_id INTEGER PRIMARY KEY AUTOINCREMENT,
    score_id INTEGER,
    pid INTEGER NOT NULL,
    process_name TEXT NOT NULL,
    action_type TEXT NOT NULL CHECK(action_type IN ('Ignore', 'Optimize', 'Restart', 'Close')),
    confirmed_at DATETIME NOT NULL,
    result_status TEXT,
    notes TEXT,
    -- No FK to waste_scores to prevent cascade deletion of action log
    FOREIGN KEY (score_id) REFERENCES waste_scores(score_id) ON DELETE SET NULL
);

-- ============================================================
-- SETTINGS (user preferences)
-- ============================================================

CREATE TABLE IF NOT EXISTS settings (
    setting_key TEXT PRIMARY KEY,
    setting_value TEXT NOT NULL,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- ============================================================
-- DAILY AGGREGATES (for historical trends beyond 7 days)
-- ============================================================

CREATE TABLE IF NOT EXISTS daily_aggregates (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    pid INTEGER NOT NULL,
    date DATE NOT NULL,
    avg_cpu REAL,
    max_memory_mb REAL,
    total_disk_io_bytes INTEGER,
    avg_idle_minutes REAL,
    waste_score_avg REAL,
    UNIQUE(pid, date)
);

-- ============================================================
-- EVALUATION RUNS (for testing metrics)
-- ============================================================

CREATE TABLE IF NOT EXISTS evaluation_runs (
    run_id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_name TEXT NOT NULL,
    started_at DATETIME NOT NULL,
    ended_at DATETIME,
    scenario_type TEXT,  -- 'active_app', 'idle_app', 'system_service', etc.
    notes TEXT
);

-- Stores ground-truth labels for evaluation
CREATE TABLE IF NOT EXISTS evaluation_labels (
    label_id INTEGER PRIMARY KEY AUTOINCREMENT,
    run_id INTEGER NOT NULL,
    pid INTEGER NOT NULL,
    actual_classification TEXT NOT NULL CHECK(actual_classification IN ('Wasteful', 'Legitimate', 'Critical')),
    detected_classification TEXT,
    detected_at DATETIME,
    FOREIGN KEY (run_id) REFERENCES evaluation_runs(run_id) ON DELETE CASCADE
);

-- ============================================================
-- INDICES FOR PERFORMANCE
-- ============================================================

CREATE INDEX IF NOT EXISTS idx_resource_samples_process_id_timestamp 
ON resource_samples(process_id, timestamp DESC);

CREATE INDEX IF NOT EXISTS idx_resource_samples_pid_timestamp 
ON resource_samples(pid, timestamp DESC);

CREATE INDEX IF NOT EXISTS idx_resource_samples_timestamp 
ON resource_samples(timestamp DESC);

CREATE INDEX IF NOT EXISTS idx_waste_scores_classification 
ON waste_scores(classification);

CREATE INDEX IF NOT EXISTS idx_processes_pid_start 
ON processes(pid, start_time DESC);

CREATE INDEX IF NOT EXISTS idx_daily_aggregates_pid_date 
ON daily_aggregates(pid, date DESC);

-- ============================================================
-- VIEWS
-- ============================================================

-- Latest sample per process with process details
CREATE VIEW IF NOT EXISTS v_latest_per_process AS
SELECT 
    p.id AS process_id,
    p.pid,
    p.name,
    p.executable_path,
    p.owner,
    p.is_critical,
    p.start_time,
    rs.sample_id,
    rs.timestamp as last_sample_time,
    rs.cpu_percent,
    rs.memory_mb,
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
) latest ON p.id = latest.process_id
INNER JOIN resource_samples rs ON rs.sample_id = latest.max_sample_id
LEFT JOIN waste_scores ws ON ws.sample_id = rs.sample_id;

-- Protected process checker (matches by name/path patterns)
CREATE VIEW IF NOT EXISTS v_protected_check AS
SELECT 
    pp.name_pattern,
    pp.path_pattern,
    pp.owner_pattern,
    pp.reason
FROM protected_processes pp;

-- ============================================================
-- SEED SETTINGS
-- ============================================================

INSERT OR IGNORE INTO settings (setting_key, setting_value) VALUES
('sampling_interval_seconds', '3'),
('retention_days_samples', '7'),
('retention_days_actions', '30'),
('waste_threshold_healthy', '3'),
('waste_threshold_suspicious', '6'),
('max_batch_size', '50'),
('vacuum_frequency_days', '7');

-- ============================================================
-- CLEANUP SCRIPTS (run via scheduled task)
-- ============================================================

-- Old samples (7 days)
-- DELETE FROM resource_samples WHERE timestamp < datetime('now', '-7 days');

-- Old user actions (30 days)  
-- DELETE FROM user_actions WHERE confirmed_at < datetime('now', '-30 days');

-- Aggregate before vacuum
-- UPDATE daily_aggregates SET ... (compute from deleted samples first)

-- Vacuum weekly, not daily
-- VACUUM;