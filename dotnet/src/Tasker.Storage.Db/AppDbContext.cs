using Microsoft.EntityFrameworkCore;
using Tasker.Storage.Db.Models;

namespace Tasker.Storage.Db;

internal class AppDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<ProjectDbModel> Projects { get; set; }
    public DbSet<TaskDbModel> Tasks { get; set; }
    public DbSet<TaskTypeDbModel> TaskTypes { get; set; }
    public DbSet<StatusDbModel> Statuses { get; set; }
    public DbSet<SeriesDbModel> Series { get; set; }
    public DbSet<TaskSeriesNumberDbModel> TaskSeriesNumbers { get; set; }
    public DbSet<LinkTypeDbModel> LinkTypes { get; set; }
    public DbSet<TaskLinkDbModel> TaskLinks { get; set; }
    public DbSet<FieldDbModel> Fields { get; set; }
    public DbSet<FieldEnumDbModel> FieldEnums { get; set; }
    public DbSet<TaskFieldDbModel> TaskFields { get; set; }
    public DbSet<TaskFieldValueDbModel> TaskFieldValues { get; set; }
    public DbSet<StatusSetDbModel> StatusSets { get; set; }
    public DbSet<BoardDbModel> Boards { get; set; }
    public DbSet<UserDbModel> Users { get; set; }
    public DbSet<ProjectMemberDbModel> ProjectMembers { get; set; }
    public DbSet<RefreshTokenDbModel> RefreshTokens { get; set; }
    public DbSet<AgentTokenDbModel> AgentTokens { get; set; }
    public DbSet<EditLockDbModel> EditLocks { get; set; }

    // Удаление:
    // - сущности проекта удаляются каскадом вместе с проектом;
    // - части одной сущности (колонки доски, статусы набора) — каскадом вместе с ней;
    // - номера задач в сериях — каскадом вместе с задачей; на серию у них ссылки нет (см. TaskSeriesNumberDbModel);
    // - ссылки между сущностями (задача → статус и т.п.) — NoAction: то, на что ссылаются, удалить нельзя.
    //   Не Restrict: он проверяется сразу и не дал бы каскадно удалить проект, а NoAction — в конце операции.
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ProjectDbModel>(x =>
        {
            x.ToTable("projects");
            x.HasKey(p => p.Id);
            x.Property(p => p.Name).HasMaxLength(100);
        });

        modelBuilder.Entity<EditLockDbModel>(x =>
        {
            x.ToTable("edit_locks");
            x.HasKey(l => new { l.Entity, l.Id });
            x.Property(l => l.Entity).HasMaxLength(20);
            x.Property(l => l.HolderKey).HasMaxLength(60);
            x.Property(l => l.HolderName).HasMaxLength(100);
            x.HasIndex(l => l.ProjectId);
        });

        ConfigureUsers(modelBuilder);
        ConfigureStatuses(modelBuilder);
        ConfigureSeries(modelBuilder);
        ConfigureTasks(modelBuilder);
        ConfigureLinks(modelBuilder);
        ConfigureFields(modelBuilder);
        ConfigureBoards(modelBuilder);
    }

    private static void ConfigureFields(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FieldEnumDbModel>(x =>
        {
            x.ToTable("field_enums");
            x.HasKey(e => e.Id);
            x.Property(e => e.Name).HasMaxLength(100);
            BelongsToProject(x, e => e.ProjectId);

            x.HasMany(e => e.Values)
                .WithOne()
                .HasForeignKey(v => v.EnumId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FieldEnumValueDbModel>(x =>
        {
            x.ToTable("field_enum_values");
            x.HasKey(v => new { v.EnumId, v.Id });
            x.Property(v => v.Name).HasMaxLength(100);
        });

        modelBuilder.Entity<FieldDbModel>(x =>
        {
            x.ToTable("fields");
            x.HasKey(f => f.Id);
            x.Property(f => f.Name).HasMaxLength(100);
            BelongsToProject(x, f => f.ProjectId);
            // Перечисление, на которое ссылается поле, удалить нельзя (NoAction).
            References<FieldDbModel, FieldEnumDbModel>(x, f => f.EnumId);
        });
    }

    private static void ConfigureUsers(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserDbModel>(x =>
        {
            x.ToTable("users");
            x.HasKey(u => u.Id);
            x.Property(u => u.Username).HasMaxLength(50);
            x.Property(u => u.NormalizedUsername).HasMaxLength(50);
            x.Property(u => u.Email).HasMaxLength(254);
            x.Property(u => u.NormalizedEmail).HasMaxLength(254);

            // Уникальность без учёта регистра — последняя линия защиты от гонки двух регистраций.
            // NULL в уникальном индексе не конфликтуют (пользователи десктопа без почты).
            x.HasIndex(u => u.NormalizedUsername).IsUnique();
            x.HasIndex(u => u.NormalizedEmail).IsUnique();

            x.Property(u => u.Kind).HasConversion<string>().HasMaxLength(10);

            // Агенты человека удаляются вместе с ним.
            x.HasOne<UserDbModel>()
                .WithMany()
                .HasForeignKey(u => u.OwnerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentTokenDbModel>(x =>
        {
            x.ToTable("agent_tokens");
            x.HasKey(t => t.Id);
            x.Property(t => t.Name).HasMaxLength(100);
            x.Property(t => t.Prefix).HasMaxLength(20);
            x.Property(t => t.TokenHash).HasMaxLength(64);
            x.HasIndex(t => t.TokenHash).IsUnique();
            x.HasIndex(t => t.AgentId);

            // Токены удаляются вместе с агентом.
            x.HasOne<UserDbModel>()
                .WithMany()
                .HasForeignKey(t => t.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectMemberDbModel>(x =>
        {
            x.ToTable("project_members");
            x.HasKey(m => new { m.ProjectId, m.UserId });
            x.HasIndex(m => m.UserId);

            // Участие удаляется и вместе с проектом, и вместе с пользователем.
            x.HasOne<ProjectDbModel>()
                .WithMany()
                .HasForeignKey(m => m.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            x.HasOne<UserDbModel>()
                .WithMany()
                .HasForeignKey(m => m.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RefreshTokenDbModel>(x =>
        {
            x.ToTable("refresh_tokens");
            x.HasKey(t => t.Id);
            x.Property(t => t.TokenHash).HasMaxLength(64);
            x.HasIndex(t => t.TokenHash).IsUnique();
            x.HasIndex(t => t.FamilyId);
            x.HasIndex(t => t.UserId);

            // Сессии удаляются вместе с пользователем.
            x.HasOne<UserDbModel>()
                .WithMany()
                .HasForeignKey(t => t.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureStatuses(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StatusDbModel>(x =>
        {
            x.ToTable("statuses");
            x.HasKey(s => s.Id);
            x.Property(s => s.Name).HasMaxLength(100);
            x.Property(s => s.Color).HasMaxLength(7);
            BelongsToProject(x, s => s.ProjectId);
        });

        modelBuilder.Entity<StatusSetDbModel>(x =>
        {
            x.ToTable("status_sets");
            x.HasKey(s => s.Id);
            x.Property(s => s.Name).HasMaxLength(100);
            BelongsToProject(x, s => s.ProjectId);

            x.HasMany(s => s.Items)
                .WithOne()
                .HasForeignKey(i => i.StatusSetId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StatusSetItemDbModel>(x =>
        {
            x.ToTable("status_set_statuses");
            x.HasKey(i => new { i.StatusSetId, i.StatusId });
            References<StatusSetItemDbModel, StatusDbModel>(x, i => i.StatusId);
        });
    }

    private static void ConfigureTasks(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskTypeDbModel>(x =>
        {
            x.ToTable("task_types");
            x.HasKey(t => t.Id);
            x.Property(t => t.Name).HasMaxLength(100);
            BelongsToProject(x, t => t.ProjectId);
            References<TaskTypeDbModel, StatusSetDbModel>(x, t => t.StatusSetId);

            x.HasMany(t => t.Fields)
                .WithOne()
                .HasForeignKey(f => f.TypeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskTypeFieldDbModel>(x =>
        {
            x.ToTable("task_type_fields");
            x.HasKey(f => new { f.TypeId, f.FieldId });
            // Поле, подключённое к типу, удалить нельзя.
            References<TaskTypeFieldDbModel, FieldDbModel>(x, f => f.FieldId);
        });

        modelBuilder.Entity<TaskFieldDbModel>(x =>
        {
            x.ToTable("task_fields");
            x.HasKey(f => new { f.TaskId, f.FieldId });
            x.Property(f => f.OwnName).HasMaxLength(100);
            x.Property(f => f.OwnKey).HasMaxLength(400);
            x.HasIndex(f => f.FieldId);
            x.HasIndex(f => f.OwnKey);

            // Поля задачи удаляются вместе с ней; перечисление собственного поля удалить нельзя. На поле каталога внешнего ключа нет
            // (FieldId общий с собственными полями): «используется ли поле» проверяет сервис в секции записи.
            x.HasOne<TaskDbModel>()
                .WithMany()
                .HasForeignKey(f => f.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
            References<TaskFieldDbModel, FieldEnumDbModel>(x, f => f.OwnEnumId);
        });

        modelBuilder.Entity<TaskFieldValueDbModel>(x =>
        {
            x.ToTable("task_field_values");
            x.HasKey(v => new { v.TaskId, v.FieldId, v.Position });
            // Фильтр задач по значению поля: равенство (значение), сравнения int и float (число); date сравнивается по тексту, тоже по первому индексу.
            x.HasIndex(v => new { v.FieldId, v.Value });
            x.HasIndex(v => new { v.FieldId, v.Number });
            // Собственные поля у каждой задачи со своим id, поэтому фильтр ищет их по имени (и типу).
            x.Property(v => v.OwnKey).HasMaxLength(400);
            x.HasIndex(v => new { v.OwnKey, v.Value });
            x.HasIndex(v => new { v.OwnKey, v.Number });

            x.HasOne<TaskFieldDbModel>()
                .WithMany()
                .HasForeignKey(v => new { v.TaskId, v.FieldId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskDbModel>(x =>
        {
            x.ToTable("tasks");
            x.HasKey(t => t.Id);
            x.Property(t => t.Title).HasMaxLength(500);
            x.HasIndex(t => new { t.ProjectId, t.CreatedAt });
            BelongsToProject(x, t => t.ProjectId);
            References<TaskDbModel, TaskTypeDbModel>(x, t => t.TypeId);
            References<TaskDbModel, StatusDbModel>(x, t => t.StatusId);
        });

        modelBuilder.Entity<TaskSeriesNumberDbModel>(x =>
        {
            x.ToTable("task_series_numbers");
            x.HasKey(n => new { n.TaskId, n.SeriesId });

            // Номер в серии не повторяется в проекте. Гонку двух процессов ловит этот индекс, сервис повторяет операцию.
            x.HasIndex(n => new { n.ProjectId, n.SeriesId, n.Number }).IsUnique();

            x.HasOne<TaskDbModel>()
                .WithMany()
                .HasForeignKey(n => n.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureLinks(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LinkTypeDbModel>(x =>
        {
            x.ToTable("link_types");
            x.HasKey(t => t.Id);
            x.Property(t => t.Name).HasMaxLength(100);
            x.Property(t => t.OutwardName).HasMaxLength(100);
            x.Property(t => t.InwardName).HasMaxLength(100);
            BelongsToProject(x, t => t.ProjectId);
        });

        modelBuilder.Entity<TaskLinkDbModel>(x =>
        {
            x.ToTable("task_links");
            x.HasKey(l => new { l.TaskId, l.TypeId, l.TargetId });
            x.HasIndex(l => l.TargetId);

            // Связи удаляются вместе с задачей-источником; по типу, у которого есть связи, удаление запрещено (NoAction).
            x.HasOne<TaskDbModel>()
                .WithMany()
                .HasForeignKey(l => l.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
            References<TaskLinkDbModel, LinkTypeDbModel>(x, l => l.TypeId);
        });
    }

    private static void ConfigureSeries(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SeriesDbModel>(x =>
        {
            x.ToTable("series");
            x.HasKey(s => s.Id);
            x.Property(s => s.Name).HasMaxLength(100);
            x.Property(s => s.Prefix).HasMaxLength(20);
            BelongsToProject(x, s => s.ProjectId);

            // Точное сравнение: у SQLite (BINARY) и Postgres (детерминированная сортировка по умолчанию) оно и так учитывает регистр.
            x.HasIndex(s => new { s.ProjectId, s.Prefix }).IsUnique();
        });
    }

    private static void ConfigureBoards(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BoardDbModel>(x =>
        {
            x.ToTable("boards");
            x.HasKey(b => b.Id);
            x.Property(b => b.Name).HasMaxLength(100);
            BelongsToProject(x, b => b.ProjectId);

            x.HasMany(b => b.StatusSets)
                .WithOne()
                .HasForeignKey(s => s.BoardId)
                .OnDelete(DeleteBehavior.Cascade);

            x.HasMany(b => b.Columns)
                .WithOne()
                .HasForeignKey(c => c.BoardId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BoardStatusSetDbModel>(x =>
        {
            x.ToTable("board_status_sets");
            x.HasKey(s => new { s.BoardId, s.StatusSetId });
            References<BoardStatusSetDbModel, StatusSetDbModel>(x, s => s.StatusSetId);
        });

        modelBuilder.Entity<BoardColumnDbModel>(x =>
        {
            x.ToTable("board_columns");
            x.HasKey(c => c.Id);
            x.Property(c => c.Name).HasMaxLength(100);

            x.HasMany(c => c.Statuses)
                .WithOne()
                .HasForeignKey(s => s.ColumnId)
                .OnDelete(DeleteBehavior.Cascade);

            x.HasMany(c => c.DropStatuses)
                .WithOne()
                .HasForeignKey(d => d.ColumnId)
                .OnDelete(DeleteBehavior.Cascade);

            x.HasMany(c => c.FieldFilters)
                .WithOne()
                .HasForeignKey(f => f.ColumnId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BoardColumnStatusDbModel>(x =>
        {
            x.ToTable("board_column_statuses");
            x.HasKey(s => new { s.ColumnId, s.StatusId });
            References<BoardColumnStatusDbModel, StatusDbModel>(x, s => s.StatusId);
        });

        modelBuilder.Entity<BoardColumnFieldFilterDbModel>(x =>
        {
            x.ToTable("board_column_field_filters");
            x.HasKey(f => new { f.ColumnId, f.Position });
            x.Property(f => f.Operator).HasMaxLength(20);
            References<BoardColumnFieldFilterDbModel, FieldDbModel>(x, f => f.FieldId);
        });

        modelBuilder.Entity<BoardColumnDropStatusDbModel>(x =>
        {
            x.ToTable("board_column_drop_statuses");
            // Один статус на набор: для каждого набора колонка задаёт ровно один целевой статус.
            x.HasKey(d => new { d.ColumnId, d.StatusSetId });
            References<BoardColumnDropStatusDbModel, StatusSetDbModel>(x, d => d.StatusSetId);
            References<BoardColumnDropStatusDbModel, StatusDbModel>(x, d => d.StatusId);
        });
    }

    private static void BelongsToProject<T>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> x,
        System.Linq.Expressions.Expression<Func<T, object?>> projectId
    ) where T : class
    {
        x.HasOne<ProjectDbModel>()
            .WithMany()
            .HasForeignKey(projectId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void References<T, TTarget>(
        Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<T> x,
        System.Linq.Expressions.Expression<Func<T, object?>> foreignKey
    ) where T : class where TTarget : class
    {
        x.HasOne<TTarget>()
            .WithMany()
            .HasForeignKey(foreignKey)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
