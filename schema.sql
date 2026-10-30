-- ============================================================
-- DAEMONGUARD DATABASE SCHEMA (CORRECTED)
-- ============================================================
-- Fixed issues:
-- - pid is now PRIMARY KEY (not surrogate id)
-- - Foreign keys reference processes.pid directly
-- - Removed broken triggers that would never fire or cause race conditions
-- - Simplified schema for reliability

PRAGMA foreign_keys = ON;

-- ============================================================
-- TABLES
-- ============================================================

-- Tracks unique processes — pid IS the primary key (no surrogate)
CREATE TABLE IF NOT EXISTS processes (
    pid INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    executable_path TEXT,
    first_seen DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_seen DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    is_protected INTEGER NOT NULL DEFAULT 0
);

-- Raw resource telemetry snapshots
-- FK references processes.pid (not surrogate id)
CREATE TABLE IF NOT EXISTS resource_samples (
    sample_id INTEGER PRIMARY KEY AUTOINCREMENT,
    pid INTEGER NOT NULL,
    timestamp DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    cpu_percent REAL,
    memory_mb REAL,
    disk_io_read_bytes INTEGER,
    disk_io_write_bytes INTEGER,
    network_in_bytes INTEGER,
    network_out_bytes INTEGER,
    gpu_percent REAL,
    idle_minutes REAL,
    FOREIGN KEY (pid) REFERENCES processes(pid) ON DELETE CASCADE
);

-- Calculated waste and risk scores per sample
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

-- Logged user mitigation actions
-- Note: sample_id here actually references waste_scores.score_id
CREATE TABLE IF NOT EXISTS user_actions (
    action_id INTEGER PRIMARY KEY AUTOINCREMENT,
    score_id INTEGER NOT NULL,
    action_type TEXT NOT NULL CHECK(action_type IN ('Ignore', 'Optimize', 'Restart', 'Close')),
    confirmed_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
    result_status TEXT,
    notes TEXT,
    FOREIGN KEY (score_id) REFERENCES waste_scores(score_id) ON DELETE CASCADE
);

-- User and system settings
CREATE TABLE IF NOT EXISTS settings (
    setting_key TEXT PRIMARY KEY,
    setting_value TEXT NOT NULL,
    updated_at DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- ============================================================
-- INDEXES
-- ============================================================

CREATE INDEX IF NOT EXISTS idx_resource_samples_pid_timestamp 
ON resource_samples(pid, timestamp DESC);

CREATE INDEX IF NOT EXISTS idx_resource_samples_timestamp 
ON resource_samples(timestamp DESC);

CREATE INDEX IF NOT EXISTS idx_waste_scores_classification 
ON waste_scores(classification);

CREATE INDEX IF NOT EXISTS idx_processes_is_protected 
ON processes(is_protected);

-- ============================================================
-- VIEWS FOR COMMON QUERIES
-- ============================================================

-- Latest sample per process (dashboard view)
CREATE VIEW IF NOT EXISTS v_latest_per_process AS
SELECT 
    p.pid,
    p.name,
    p.is_protected,
    rs.cpu_percent,
    rs.memory_mb,
    rs.timestamp as last_sample_time,
    ws.waste_score,
    ws.classification,
    ws.explanation_text
FROM processes p
INNER JOIN (
    SELECT pid, MAX(sample_id) AS max_sample_id
    FROM resource_samples
    GROUP BY pid
) latest ON p.pid = latest.pid
INNER JOIN resource_samples rs ON rs.sample_id = latest.max_sample_id
LEFT JOIN waste_scores ws ON ws.sample_id = rs.sample_id;

-- ============================================================
-- SEED SETTINGS
-- ============================================================

INSERT OR IGNORE INTO settings (setting_key, setting_value) VALUES
('sampling_interval_seconds', '3'),
('retention_days_samples', '7'),
('retention_days_actions', '30'),
('waste_threshold_healthy', '3'),
('waste_threshold_suspicious', '6'),
('max_batch_size', '50');

-- ============================================================
-- MANUAL CLEANUP SCRIPT
-- Run this as a scheduled daily task
-- ============================================================

-- DELETE FROM resource_samples WHERE timestamp < datetime('now', '-7 days');
-- DELETE FROM user_actions WHERE confirmed_at < datetime('now', '-30 days');
-- DELETE FROM processes WHERE last_seen < datetime('now', '-7 days') AND is_protected = 0;