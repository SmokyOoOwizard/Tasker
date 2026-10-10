CREATE TABLE IF NOT EXISTS files (
    path       TEXT PRIMARY KEY,   -- относительно .tasker, через «/»
    size       INTEGER NOT NULL,
    mtime      INTEGER NOT NULL,   -- ticks UTC
    kind       TEXT NOT NULL,
    project_id TEXT,
    id         TEXT NOT NULL,
    sort_text  TEXT,               -- имя (у пользователя — нормализованное; у задачи — заголовок в нижнем регистре, для упорядочивания)
    sort_num   INTEGER,            -- у задачи — createdAt, ticks UTC
    sort_updated INTEGER,          -- у задачи — updatedAt, ticks UTC
    type_id    TEXT,
    status_id  TEXT,
    user_kind  TEXT,
    data       TEXT,               -- сущность целиком, JSON; null, если файл не прочитан
    error      TEXT                -- почему файл не прочитан; такие файлы не попадают в списки
);
CREATE INDEX ix_files_by_name ON files (kind, project_id, sort_text, id);
CREATE INDEX ix_files_by_created ON files (kind, project_id, sort_num, id);
CREATE INDEX ix_files_by_updated ON files (kind, project_id, sort_updated, sort_num, id);
CREATE INDEX ix_files_by_status ON files (kind, project_id, status_id);
CREATE TABLE IF NOT EXISTS task_series (
    path          TEXT NOT NULL,      -- файл задачи, как в files
    project_id    TEXT NOT NULL,
    series_id     TEXT NOT NULL,
    number        INTEGER NOT NULL,
    task_id       TEXT NOT NULL,
    created_ticks INTEGER NOT NULL    -- createdAt задачи, ticks UTC
);
CREATE INDEX ix_task_series_by_number ON task_series (project_id, series_id, number);
CREATE INDEX ix_task_series_by_path ON task_series (path);
CREATE TABLE IF NOT EXISTS task_links (
    path       TEXT NOT NULL,         -- файл задачи-источника, как в files
    project_id TEXT NOT NULL,
    type_id    TEXT NOT NULL,         -- тип связи
    source_id  TEXT NOT NULL,
    target_id  TEXT NOT NULL
);
CREATE INDEX ix_task_links_by_target ON task_links (project_id, target_id);
CREATE INDEX ix_task_links_by_source ON task_links (project_id, type_id, source_id);
CREATE INDEX ix_task_links_by_path ON task_links (path);
CREATE TABLE IF NOT EXISTS task_fields (
    path       TEXT NOT NULL,         -- файл задачи, как в files
    project_id TEXT NOT NULL,
    field_id   TEXT NOT NULL,         -- поле каталога или собственное поле задачи
    enum_id    TEXT,                  -- перечисление собственного поля; у поля каталога null
    own_name   TEXT,                  -- собственное поле: имя в нижнем регистре (FieldNames.Key); у поля каталога null
    own_type   INTEGER                -- собственное поле: тип (FieldType); у поля каталога null
);
CREATE INDEX ix_task_fields_by_field ON task_fields (project_id, field_id);
CREATE INDEX ix_task_fields_by_enum ON task_fields (project_id, enum_id);
CREATE INDEX ix_task_fields_by_own_name ON task_fields (own_name);
CREATE INDEX ix_task_fields_by_path ON task_fields (path);
CREATE TABLE IF NOT EXISTS task_field_values (
    path       TEXT NOT NULL,         -- файл задачи, как в files
    project_id TEXT NOT NULL,
    field_id   TEXT NOT NULL,
    value      TEXT NOT NULL,
    number     REAL,                  -- значение числом (FieldNumbers); null, если оно не число. Сравнения int и float идут по нему
    own_name   TEXT,                  -- собственное поле: имя в нижнем регистре (FieldNames.Key); у поля каталога null
    own_type   INTEGER                -- собственное поле: тип (FieldType); у поля каталога null
);
CREATE INDEX ix_task_field_values_by_value ON task_field_values (field_id, value);
CREATE INDEX ix_task_field_values_by_number ON task_field_values (field_id, number);
CREATE INDEX ix_task_field_values_by_own_value ON task_field_values (own_name, value);
CREATE INDEX ix_task_field_values_by_own_number ON task_field_values (own_name, number);
CREATE INDEX ix_task_field_values_by_path ON task_field_values (path);
