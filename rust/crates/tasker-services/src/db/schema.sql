CREATE TABLE "edit_locks" (
    "Entity" TEXT NOT NULL,
    "Id" TEXT NOT NULL,
    "HolderKey" TEXT NOT NULL,
    "HolderName" TEXT NOT NULL,
    "ProjectId" TEXT NULL,
    "AcquiredAt" TEXT NOT NULL,
    "ExpiresAt" TEXT NOT NULL,
    CONSTRAINT "PK_edit_locks" PRIMARY KEY ("Entity", "Id")
);
CREATE TABLE "projects" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_projects" PRIMARY KEY,
    "Name" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "Version" INTEGER NOT NULL
);
CREATE TABLE "users" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_users" PRIMARY KEY,
    "Username" TEXT NOT NULL,
    "NormalizedUsername" TEXT NOT NULL,
    "Email" TEXT NULL,
    "NormalizedEmail" TEXT NULL,
    "PasswordHash" TEXT NULL,
    "SecurityStamp" TEXT NOT NULL,
    "IsAdmin" INTEGER NOT NULL,
    "Kind" TEXT NOT NULL,
    "OwnerId" TEXT NULL,
    "CreatedAt" TEXT NOT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "FK_users_users_OwnerId" FOREIGN KEY ("OwnerId") REFERENCES "users" ("Id") ON DELETE CASCADE
);
CREATE TABLE "boards" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_boards" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "FK_boards_projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "projects" ("Id") ON DELETE CASCADE
);
CREATE TABLE "field_enums" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_field_enums" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "FK_field_enums_projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "projects" ("Id") ON DELETE CASCADE
);
CREATE TABLE "link_types" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_link_types" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "OutwardName" TEXT NOT NULL,
    "InwardName" TEXT NOT NULL,
    "AllowCycles" INTEGER NOT NULL,
    "Hierarchical" INTEGER NOT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "FK_link_types_projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "projects" ("Id") ON DELETE CASCADE
);
CREATE TABLE "series" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_series" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Prefix" TEXT NOT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "FK_series_projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "projects" ("Id") ON DELETE CASCADE
);
CREATE TABLE "status_sets" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_status_sets" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "FK_status_sets_projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "projects" ("Id") ON DELETE CASCADE
);
CREATE TABLE "statuses" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_statuses" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Color" TEXT NOT NULL,
    "Description" TEXT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "FK_statuses_projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "projects" ("Id") ON DELETE CASCADE
);
CREATE TABLE "agent_tokens" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_agent_tokens" PRIMARY KEY,
    "AgentId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Prefix" TEXT NOT NULL,
    "TokenHash" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "ExpiresAt" TEXT NULL,
    "LastUsedAt" TEXT NULL,
    "RevokedAt" TEXT NULL,
    CONSTRAINT "FK_agent_tokens_users_AgentId" FOREIGN KEY ("AgentId") REFERENCES "users" ("Id") ON DELETE CASCADE
);
CREATE TABLE "project_members" (
    "ProjectId" TEXT NOT NULL,
    "UserId" TEXT NOT NULL,
    CONSTRAINT "PK_project_members" PRIMARY KEY ("ProjectId", "UserId"),
    CONSTRAINT "FK_project_members_projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "projects" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_project_members_users_UserId" FOREIGN KEY ("UserId") REFERENCES "users" ("Id") ON DELETE CASCADE
);
CREATE TABLE "refresh_tokens" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_refresh_tokens" PRIMARY KEY,
    "UserId" TEXT NOT NULL,
    "FamilyId" TEXT NOT NULL,
    "TokenHash" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "ExpiresAt" TEXT NOT NULL,
    "RevokedAt" TEXT NULL,
    "FamilyRevokedAt" TEXT NULL,
    CONSTRAINT "FK_refresh_tokens_users_UserId" FOREIGN KEY ("UserId") REFERENCES "users" ("Id") ON DELETE CASCADE
);
CREATE TABLE "board_columns" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_board_columns" PRIMARY KEY,
    "BoardId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Position" INTEGER NOT NULL,
    CONSTRAINT "FK_board_columns_boards_BoardId" FOREIGN KEY ("BoardId") REFERENCES "boards" ("Id") ON DELETE CASCADE
);
CREATE TABLE "field_enum_values" (
    "EnumId" TEXT NOT NULL,
    "Id" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Position" INTEGER NOT NULL,
    CONSTRAINT "PK_field_enum_values" PRIMARY KEY ("EnumId", "Id"),
    CONSTRAINT "FK_field_enum_values_field_enums_EnumId" FOREIGN KEY ("EnumId") REFERENCES "field_enums" ("Id") ON DELETE CASCADE
);
CREATE TABLE "fields" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_fields" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Type" INTEGER NOT NULL,
    "Multiple" INTEGER NOT NULL,
    "EnumId" TEXT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "FK_fields_field_enums_EnumId" FOREIGN KEY ("EnumId") REFERENCES "field_enums" ("Id"),
    CONSTRAINT "FK_fields_projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "projects" ("Id") ON DELETE CASCADE
);
CREATE TABLE "board_status_sets" (
    "BoardId" TEXT NOT NULL,
    "StatusSetId" TEXT NOT NULL,
    "Position" INTEGER NOT NULL,
    CONSTRAINT "PK_board_status_sets" PRIMARY KEY ("BoardId", "StatusSetId"),
    CONSTRAINT "FK_board_status_sets_boards_BoardId" FOREIGN KEY ("BoardId") REFERENCES "boards" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_board_status_sets_status_sets_StatusSetId" FOREIGN KEY ("StatusSetId") REFERENCES "status_sets" ("Id")
);
CREATE TABLE "task_types" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_task_types" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "Name" TEXT NOT NULL,
    "Description" TEXT NULL,
    "StatusSetId" TEXT NOT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "FK_task_types_projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "projects" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_task_types_status_sets_StatusSetId" FOREIGN KEY ("StatusSetId") REFERENCES "status_sets" ("Id")
);
CREATE TABLE "status_set_statuses" (
    "StatusSetId" TEXT NOT NULL,
    "StatusId" TEXT NOT NULL,
    "Position" INTEGER NOT NULL,
    CONSTRAINT "PK_status_set_statuses" PRIMARY KEY ("StatusSetId", "StatusId"),
    CONSTRAINT "FK_status_set_statuses_status_sets_StatusSetId" FOREIGN KEY ("StatusSetId") REFERENCES "status_sets" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_status_set_statuses_statuses_StatusId" FOREIGN KEY ("StatusId") REFERENCES "statuses" ("Id")
);
CREATE TABLE "board_column_drop_statuses" (
    "ColumnId" TEXT NOT NULL,
    "StatusSetId" TEXT NOT NULL,
    "StatusId" TEXT NOT NULL,
    CONSTRAINT "PK_board_column_drop_statuses" PRIMARY KEY ("ColumnId", "StatusSetId"),
    CONSTRAINT "FK_board_column_drop_statuses_board_columns_ColumnId" FOREIGN KEY ("ColumnId") REFERENCES "board_columns" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_board_column_drop_statuses_status_sets_StatusSetId" FOREIGN KEY ("StatusSetId") REFERENCES "status_sets" ("Id"),
    CONSTRAINT "FK_board_column_drop_statuses_statuses_StatusId" FOREIGN KEY ("StatusId") REFERENCES "statuses" ("Id")
);
CREATE TABLE "board_column_statuses" (
    "ColumnId" TEXT NOT NULL,
    "StatusId" TEXT NOT NULL,
    "Position" INTEGER NOT NULL,
    CONSTRAINT "PK_board_column_statuses" PRIMARY KEY ("ColumnId", "StatusId"),
    CONSTRAINT "FK_board_column_statuses_board_columns_ColumnId" FOREIGN KEY ("ColumnId") REFERENCES "board_columns" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_board_column_statuses_statuses_StatusId" FOREIGN KEY ("StatusId") REFERENCES "statuses" ("Id")
);
CREATE TABLE "board_column_field_filters" (
    "ColumnId" TEXT NOT NULL,
    "Position" INTEGER NOT NULL,
    "FieldId" TEXT NOT NULL,
    "Operator" TEXT NOT NULL,
    "Value" TEXT NULL,
    CONSTRAINT "PK_board_column_field_filters" PRIMARY KEY ("ColumnId", "Position"),
    CONSTRAINT "FK_board_column_field_filters_board_columns_ColumnId" FOREIGN KEY ("ColumnId") REFERENCES "board_columns" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_board_column_field_filters_fields_FieldId" FOREIGN KEY ("FieldId") REFERENCES "fields" ("Id")
);
CREATE TABLE "task_type_fields" (
    "TypeId" TEXT NOT NULL,
    "FieldId" TEXT NOT NULL,
    "Required" INTEGER NOT NULL,
    "Position" INTEGER NOT NULL,
    CONSTRAINT "PK_task_type_fields" PRIMARY KEY ("TypeId", "FieldId"),
    CONSTRAINT "FK_task_type_fields_fields_FieldId" FOREIGN KEY ("FieldId") REFERENCES "fields" ("Id"),
    CONSTRAINT "FK_task_type_fields_task_types_TypeId" FOREIGN KEY ("TypeId") REFERENCES "task_types" ("Id") ON DELETE CASCADE
);
CREATE TABLE "tasks" (
    "Id" TEXT NOT NULL CONSTRAINT "PK_tasks" PRIMARY KEY,
    "ProjectId" TEXT NOT NULL,
    "Title" TEXT NOT NULL,
    "Description" TEXT NULL,
    "TypeId" TEXT NOT NULL,
    "StatusId" TEXT NOT NULL,
    "CreatedAt" TEXT NOT NULL,
    "UpdatedAt" TEXT NOT NULL,
    "Version" INTEGER NOT NULL,
    CONSTRAINT "FK_tasks_projects_ProjectId" FOREIGN KEY ("ProjectId") REFERENCES "projects" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_tasks_statuses_StatusId" FOREIGN KEY ("StatusId") REFERENCES "statuses" ("Id"),
    CONSTRAINT "FK_tasks_task_types_TypeId" FOREIGN KEY ("TypeId") REFERENCES "task_types" ("Id")
);
CREATE TABLE "task_fields" (
    "TaskId" TEXT NOT NULL,
    "FieldId" TEXT NOT NULL,
    "Position" INTEGER NOT NULL,
    "OwnName" TEXT NULL,
    "OwnKey" TEXT NULL,
    "OwnType" INTEGER NULL,
    "OwnRequired" INTEGER NOT NULL,
    "OwnMultiple" INTEGER NOT NULL,
    "OwnEnumId" TEXT NULL,
    CONSTRAINT "PK_task_fields" PRIMARY KEY ("TaskId", "FieldId"),
    CONSTRAINT "FK_task_fields_field_enums_OwnEnumId" FOREIGN KEY ("OwnEnumId") REFERENCES "field_enums" ("Id"),
    CONSTRAINT "FK_task_fields_tasks_TaskId" FOREIGN KEY ("TaskId") REFERENCES "tasks" ("Id") ON DELETE CASCADE
);
CREATE TABLE "task_links" (
    "TaskId" TEXT NOT NULL,
    "TypeId" TEXT NOT NULL,
    "TargetId" TEXT NOT NULL,
    CONSTRAINT "PK_task_links" PRIMARY KEY ("TaskId", "TypeId", "TargetId"),
    CONSTRAINT "FK_task_links_link_types_TypeId" FOREIGN KEY ("TypeId") REFERENCES "link_types" ("Id"),
    CONSTRAINT "FK_task_links_tasks_TaskId" FOREIGN KEY ("TaskId") REFERENCES "tasks" ("Id") ON DELETE CASCADE
);
CREATE TABLE "task_series_numbers" (
    "TaskId" TEXT NOT NULL,
    "SeriesId" TEXT NOT NULL,
    "ProjectId" TEXT NOT NULL,
    "Number" INTEGER NOT NULL,
    CONSTRAINT "PK_task_series_numbers" PRIMARY KEY ("TaskId", "SeriesId"),
    CONSTRAINT "FK_task_series_numbers_tasks_TaskId" FOREIGN KEY ("TaskId") REFERENCES "tasks" ("Id") ON DELETE CASCADE
);
CREATE TABLE "task_field_values" (
    "TaskId" TEXT NOT NULL,
    "FieldId" TEXT NOT NULL,
    "Position" INTEGER NOT NULL,
    "Value" TEXT NOT NULL,
    "Number" REAL NULL,
    "OwnKey" TEXT NULL,
    "OwnType" INTEGER NULL,
    CONSTRAINT "PK_task_field_values" PRIMARY KEY ("TaskId", "FieldId", "Position"),
    CONSTRAINT "FK_task_field_values_task_fields_TaskId_FieldId" FOREIGN KEY ("TaskId", "FieldId") REFERENCES "task_fields" ("TaskId", "FieldId") ON DELETE CASCADE
);
CREATE INDEX "IX_agent_tokens_AgentId" ON "agent_tokens" ("AgentId");
CREATE UNIQUE INDEX "IX_agent_tokens_TokenHash" ON "agent_tokens" ("TokenHash");
CREATE INDEX "IX_board_column_drop_statuses_StatusId" ON "board_column_drop_statuses" ("StatusId");
CREATE INDEX "IX_board_column_drop_statuses_StatusSetId" ON "board_column_drop_statuses" ("StatusSetId");
CREATE INDEX "IX_board_column_field_filters_FieldId" ON "board_column_field_filters" ("FieldId");
CREATE INDEX "IX_board_column_statuses_StatusId" ON "board_column_statuses" ("StatusId");
CREATE INDEX "IX_board_columns_BoardId" ON "board_columns" ("BoardId");
CREATE INDEX "IX_board_status_sets_StatusSetId" ON "board_status_sets" ("StatusSetId");
CREATE INDEX "IX_boards_ProjectId" ON "boards" ("ProjectId");
CREATE INDEX "IX_edit_locks_ProjectId" ON "edit_locks" ("ProjectId");
CREATE INDEX "IX_field_enums_ProjectId" ON "field_enums" ("ProjectId");
CREATE INDEX "IX_fields_EnumId" ON "fields" ("EnumId");
CREATE INDEX "IX_fields_ProjectId" ON "fields" ("ProjectId");
CREATE INDEX "IX_link_types_ProjectId" ON "link_types" ("ProjectId");
CREATE INDEX "IX_project_members_UserId" ON "project_members" ("UserId");
CREATE INDEX "IX_refresh_tokens_FamilyId" ON "refresh_tokens" ("FamilyId");
CREATE UNIQUE INDEX "IX_refresh_tokens_TokenHash" ON "refresh_tokens" ("TokenHash");
CREATE INDEX "IX_refresh_tokens_UserId" ON "refresh_tokens" ("UserId");
CREATE UNIQUE INDEX "IX_series_ProjectId_Prefix" ON "series" ("ProjectId", "Prefix");
CREATE INDEX "IX_status_set_statuses_StatusId" ON "status_set_statuses" ("StatusId");
CREATE INDEX "IX_status_sets_ProjectId" ON "status_sets" ("ProjectId");
CREATE INDEX "IX_statuses_ProjectId" ON "statuses" ("ProjectId");
CREATE INDEX "IX_task_field_values_FieldId_Number" ON "task_field_values" ("FieldId", "Number");
CREATE INDEX "IX_task_field_values_FieldId_Value" ON "task_field_values" ("FieldId", "Value");
CREATE INDEX "IX_task_field_values_OwnKey_Number" ON "task_field_values" ("OwnKey", "Number");
CREATE INDEX "IX_task_field_values_OwnKey_Value" ON "task_field_values" ("OwnKey", "Value");
CREATE INDEX "IX_task_fields_FieldId" ON "task_fields" ("FieldId");
CREATE INDEX "IX_task_fields_OwnEnumId" ON "task_fields" ("OwnEnumId");
CREATE INDEX "IX_task_fields_OwnKey" ON "task_fields" ("OwnKey");
CREATE INDEX "IX_task_links_TargetId" ON "task_links" ("TargetId");
CREATE INDEX "IX_task_links_TypeId" ON "task_links" ("TypeId");
CREATE UNIQUE INDEX "IX_task_series_numbers_ProjectId_SeriesId_Number" ON "task_series_numbers" ("ProjectId", "SeriesId", "Number");
CREATE INDEX "IX_task_type_fields_FieldId" ON "task_type_fields" ("FieldId");
CREATE INDEX "IX_task_types_ProjectId" ON "task_types" ("ProjectId");
CREATE INDEX "IX_task_types_StatusSetId" ON "task_types" ("StatusSetId");
CREATE INDEX "IX_tasks_ProjectId_CreatedAt" ON "tasks" ("ProjectId", "CreatedAt");
CREATE INDEX "IX_tasks_StatusId" ON "tasks" ("StatusId");
CREATE INDEX "IX_tasks_TypeId" ON "tasks" ("TypeId");
CREATE UNIQUE INDEX "IX_users_NormalizedEmail" ON "users" ("NormalizedEmail");
CREATE UNIQUE INDEX "IX_users_NormalizedUsername" ON "users" ("NormalizedUsername");
CREATE INDEX "IX_users_OwnerId" ON "users" ("OwnerId");
