-- JSQ Experiment Database Schema
-- SQLite 3.x, WAL mode recommended

-- Таблица экспериментов
CREATE TABLE IF NOT EXISTS experiments (
    id TEXT PRIMARY KEY,
    post_id TEXT, -- A/B/C
    name TEXT NOT NULL,
    part_number TEXT,
    operator TEXT,
    refrigerant TEXT,
    state TEXT NOT NULL DEFAULT 'Idle', -- Idle, Running, Paused, Stopped, Finalized, Recovered
    start_time TEXT, -- ISO 8601 format
    end_time TEXT,
    post_a_enabled INTEGER DEFAULT 1,
    post_b_enabled INTEGER DEFAULT 1,
    post_c_enabled INTEGER DEFAULT 1,
    batch_size INTEGER DEFAULT 500,
    aggregation_interval_sec INTEGER DEFAULT 20,
    checkpoint_interval_sec INTEGER DEFAULT 30,
    created_at TEXT DEFAULT (datetime('now')),
    updated_at TEXT DEFAULT (datetime('now'))
);

-- Таблица операторов
CREATE TABLE IF NOT EXISTS operators (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    created_at TEXT DEFAULT (datetime('now'))
);

-- Конфигурация каналов
CREATE TABLE IF NOT EXISTS channel_config (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    experiment_id TEXT NOT NULL,
    channel_index INTEGER NOT NULL,
    channel_name TEXT NOT NULL,
    channel_unit TEXT,
    channel_group TEXT, -- PostA, PostB, PostC, Common, System
    channel_type TEXT, -- Pressure, Temperature, Electrical, Flow, Humidity, CurrentLoop, System
    min_limit REAL,
    max_limit REAL,
    enabled INTEGER DEFAULT 1,
    high_precision INTEGER DEFAULT 0, -- 1 -> 10s, 0 -> 20s
    agg_interval_sec INTEGER DEFAULT 20,
    FOREIGN KEY (experiment_id) REFERENCES experiments(id) ON DELETE CASCADE,
    UNIQUE(experiment_id, channel_index)
);

-- Сырые измерения (высокочастотные данные)
CREATE TABLE IF NOT EXISTS raw_samples (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    experiment_id TEXT NOT NULL,
    timestamp TEXT NOT NULL, -- ISO 8601 format
    channel_index INTEGER NOT NULL,
    value REAL NOT NULL,
    is_valid INTEGER DEFAULT 1, -- 0 если -99 или ошибка
    created_at TEXT DEFAULT (datetime('now')),
    FOREIGN KEY (experiment_id) REFERENCES experiments(id) ON DELETE CASCADE
);

-- Индексы для raw_samples
CREATE INDEX IF NOT EXISTS idx_raw_samples_experiment_timestamp 
    ON raw_samples(experiment_id, timestamp);
CREATE INDEX IF NOT EXISTS idx_raw_samples_channel 
    ON raw_samples(experiment_id, channel_index);

-- Агрегированные данные (20 секунд)
CREATE TABLE IF NOT EXISTS agg_samples_20s (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    experiment_id TEXT NOT NULL,
    timestamp TEXT NOT NULL, -- Начало 20-секундного окна
    channel_index INTEGER NOT NULL,
    value_min REAL,
    value_max REAL,
    value_avg REAL,
    sample_count INTEGER,
    invalid_count INTEGER,
    quality_flag INTEGER DEFAULT 1, -- 1 = OK, 0 = degraded
    agg_window_sec INTEGER DEFAULT 20,
    created_at TEXT DEFAULT (datetime('now')),
    FOREIGN KEY (experiment_id) REFERENCES experiments(id) ON DELETE CASCADE,
    UNIQUE(experiment_id, timestamp, channel_index)
);

-- Индексы для agg_samples_20s
CREATE INDEX IF NOT EXISTS idx_agg_samples_experiment_timestamp 
    ON agg_samples_20s(experiment_id, timestamp);

-- События аномалий
CREATE TABLE IF NOT EXISTS anomaly_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    experiment_id TEXT NOT NULL,
    timestamp TEXT NOT NULL,
    channel_index INTEGER NOT NULL,
    channel_name TEXT NOT NULL,
    anomaly_type TEXT NOT NULL, -- MinViolation, MaxViolation, Delta, NoData
    value REAL,
    threshold REAL,
    duration_sec INTEGER,
    is_acknowledged INTEGER DEFAULT 0,
    acknowledged_at TEXT,
    acknowledged_by TEXT,
    context_json TEXT, -- JSON с дополнительным контекстом
    created_at TEXT DEFAULT (datetime('now')),
    FOREIGN KEY (experiment_id) REFERENCES experiments(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_anomaly_events_experiment 
    ON anomaly_events(experiment_id, timestamp);

-- Системные события
CREATE TABLE IF NOT EXISTS system_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    experiment_id TEXT,
    timestamp TEXT DEFAULT (datetime('now')),
    event_type TEXT NOT NULL, -- SystemStart, SystemStop, ExperimentStart, ExperimentStop, Error, Warning, Info, Checkpoint
    severity TEXT DEFAULT 'Info', -- Debug, Info, Warning, Error, Critical
    message TEXT NOT NULL,
    source TEXT, -- модуль/компонент
    correlation_id TEXT,
    details_json TEXT, -- JSON с деталями
    FOREIGN KEY (experiment_id) REFERENCES experiments(id) ON DELETE SET NULL
);

CREATE INDEX IF NOT EXISTS idx_system_events_experiment_timestamp 
    ON system_events(experiment_id, timestamp);
CREATE INDEX IF NOT EXISTS idx_system_events_type 
    ON system_events(event_type);

-- Таблица чекпоинтов для восстановления
CREATE TABLE IF NOT EXISTS checkpoints (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    experiment_id TEXT NOT NULL,
    checkpoint_time TEXT NOT NULL,
    last_sample_timestamp TEXT,
    last_sample_id INTEGER,
    queue_state_json TEXT, -- состояние очередей
    statistics_json TEXT, -- статистика на момент чекпоинта
    FOREIGN KEY (experiment_id) REFERENCES experiments(id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_checkpoints_experiment 
    ON checkpoints(experiment_id, checkpoint_time DESC);
