using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;

namespace Openza.Tasks.Tests;

public sealed class SqliteTaskStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "openza-tasks-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Initialize_creates_default_projects_and_integrations()
    {
        var store = CreateStore();
        await store.InitializeAsync();

        var projects = await store.GetProjectsAsync();
        var integrations = await store.GetIntegrationsAsync();

        Assert.DoesNotContain(projects, p => p.Id == "proj_inbox");
        Assert.Contains(projects, p => p.Id == "proj_work");
        Assert.Contains(projects, p => p.Id == "proj_personal");
        Assert.Contains(integrations, i => i.Id == IntegrationIds.Todoist);
        Assert.Contains(integrations, i => i.Id == IntegrationIds.MicrosoftToDo);
        Assert.DoesNotContain(integrations, i => i.Id == IntegrationIds.Obsidian);
    }

    [Fact]
    public async Task UpsertTask_roundtrips_labels_and_filters_today()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var label = new LabelItem { Id = "label_test", Name = "test", IntegrationId = IntegrationIds.Local };
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_1",
            Title = "Today task",
            Priority = 1,
            DueDate = DateTimeOffset.Now,
            Labels = [label],
        });

        var today = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Today });

        var task = Assert.Single(today);
        Assert.Equal("Today task", task.Title);
        Assert.Equal("test", Assert.Single(task.Labels).Name);
    }

    [Fact]
    public async Task GetTasks_filters_by_label()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        var target = new LabelItem { Id = "label_target", Name = "target", IntegrationId = IntegrationIds.Local };
        var other = new LabelItem { Id = "label_other", Name = "other", IntegrationId = IntegrationIds.Local };

        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_target",
            Title = "Target task",
            Labels = [target],
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_other",
            Title = "Other task",
            Labels = [other],
        });

        var tasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All, LabelId = "label_target" });

        var task = Assert.Single(tasks);
        Assert.Equal("task_target", task.Id);
    }

    [Fact]
    public async Task UpsertTask_reuses_existing_label_name_when_unique_name_index_exists()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = store.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS ux_labels_name_test ON labels(name)";
            await command.ExecuteNonQueryAsync();
        }

        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_existing_label",
            Title = "Existing label",
            Labels =
            [
                new LabelItem
                {
                    Id = "label_new_important",
                    Name = "important",
                    IntegrationId = IntegrationIds.Local,
                },
            ],
        });

        var task = Assert.Single(await store.GetTasksAsync(new TaskQuery { LabelId = "label_important" }));
        Assert.Equal("task_existing_label", task.Id);
        Assert.Equal("label_important", Assert.Single(task.Labels).Id);
    }

    [Fact]
    public async Task NextActions_shows_only_explicit_next_tasks()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_next",
            Title = "Next task",
            Status = TaskItemStatus.Next,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_labeled",
            Title = "Labeled but not next",
            Labels = [new LabelItem { Id = "label_next", Name = "next", IntegrationId = IntegrationIds.Local }],
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_done",
            Title = "Completed next task",
            Status = TaskItemStatus.Completed,
        });

        var tasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.NextActions });

        var task = Assert.Single(tasks);
        Assert.Equal("task_next", task.Id);
    }

    [Fact]
    public async Task Inbox_shows_unlisted_tasks_without_project()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_inbox",
            Title = "Clarify me",
            Status = TaskItemStatus.None,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_project",
            Title = "Project support",
            ProjectId = "proj_work",
            Status = TaskItemStatus.None,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_waiting",
            Title = "Waiting",
            Status = TaskItemStatus.Waiting,
        });

        var tasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.Inbox });

        var task = Assert.Single(tasks);
        Assert.Equal("task_inbox", task.Id);
    }

    [Fact]
    public async Task GetTaskCounts_returns_smart_list_and_project_counts()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project_a",
            Name = "Project A",
            IntegrationId = IntegrationIds.Local,
        });
        await store.UpsertProjectAsync(new ProjectItem
        {
            Id = "project_b",
            Name = "Project B",
            IntegrationId = IntegrationIds.Local,
        });

        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_today",
            Title = "Today",
            ProjectId = "project_a",
            DueDate = DateTimeOffset.Now,
            Status = TaskItemStatus.Next,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_overdue",
            Title = "Overdue",
            ProjectId = "project_a",
            DueDate = DateTimeOffset.Now.AddDays(-1),
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_inbox",
            Title = "Inbox",
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_waiting",
            Title = "Waiting",
            Status = TaskItemStatus.Waiting,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_someday",
            Title = "Someday",
            Status = TaskItemStatus.Someday,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_done",
            Title = "Completed",
            ProjectId = "project_a",
            Status = TaskItemStatus.Completed,
        });
        await store.UpsertTaskAsync(new TaskItem
        {
            Id = "task_other",
            Title = "Other project",
            ProjectId = "project_b",
        });

        var counts = await store.GetTaskCountsAsync();

        Assert.Equal(1, counts.Inbox);
        Assert.Equal(1, counts.NextActions);
        Assert.Equal(1, counts.Waiting);
        Assert.Equal(1, counts.Someday);
        Assert.Equal(1, counts.Today);
        Assert.Equal(1, counts.Overdue);
        Assert.Equal(6, counts.Open);
        Assert.Equal(7, counts.All);
        Assert.Equal(1, counts.Completed);
        Assert.Equal(2, counts.ActiveByProject["project_a"]);
        Assert.Equal(1, counts.ActiveByProject["project_b"]);
    }

    [Fact]
    public async Task QueueCompletion_roundtrips_and_mark_synced_removes()
    {
        var store = CreateStore();
        await store.InitializeAsync();
        await store.QueueCompletionAsync(new PendingCompletion
        {
            Id = "completion_1",
            TaskId = "task_1",
            Provider = IntegrationIds.Todoist,
            ProviderTaskId = "remote_1",
            Completed = true,
        });

        var pending = await store.GetPendingCompletionsAsync(IntegrationIds.Todoist);
        Assert.Single(pending);

        await store.MarkCompletionSyncedAsync("completion_1");

        Assert.Empty(await store.GetPendingCompletionsAsync(IntegrationIds.Todoist));
    }

    [Fact]
    public async Task Initialize_upgrades_legacy_flutter_schema()
    {
        Directory.CreateDirectory(_directory);
        var databasePath = Path.Combine(_directory, "legacy-openza.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE integrations (
                  id TEXT PRIMARY KEY NOT NULL,
                  name TEXT NOT NULL,
                  is_active INTEGER NOT NULL DEFAULT 0,
                  created_at INTEGER NOT NULL
                );

                CREATE TABLE projects (
                  id TEXT PRIMARY KEY NOT NULL,
                  name TEXT NOT NULL,
                  description TEXT,
                  color TEXT,
                  icon TEXT,
                  parent_id TEXT,
                  sort_order INTEGER NOT NULL DEFAULT 0,
                  created_at INTEGER NOT NULL,
                  updated_at INTEGER
                );

                CREATE TABLE tasks (
                  id TEXT PRIMARY KEY NOT NULL,
                  title TEXT NOT NULL,
                  description TEXT,
                  project_id TEXT,
                  parent_id TEXT,
                  priority INTEGER NOT NULL DEFAULT 2,
                  status TEXT NOT NULL DEFAULT 'pending',
                  due_date INTEGER,
                  due_time TEXT,
                  notes TEXT,
                  integrations TEXT,
                  created_at INTEGER NOT NULL,
                  updated_at INTEGER,
                  completed_at INTEGER
                );

                CREATE TABLE labels (
                  id TEXT PRIMARY KEY NOT NULL,
                  name TEXT NOT NULL,
                  color TEXT,
                  description TEXT,
                  sort_order INTEGER NOT NULL DEFAULT 0,
                  integrations TEXT,
                  created_at INTEGER NOT NULL
                );

                CREATE TABLE task_labels (
                  task_id TEXT NOT NULL,
                  label_id TEXT NOT NULL,
                  PRIMARY KEY (task_id, label_id)
                );

                INSERT INTO projects (id, name, created_at) VALUES ('proj_old', 'Old Project', 1710000000);
                INSERT INTO tasks (id, title, project_id, status, created_at)
                VALUES ('task_old', 'Legacy local task', 'proj_old', 'pending', 1710000000);
                """;
            await command.ExecuteNonQueryAsync();
        }

        var store = new SqliteTaskStore(databasePath);
        await store.InitializeAsync();

        var tasks = await store.GetTasksAsync(new TaskQuery { Kind = TaskListKind.All });
        var task = Assert.Single(tasks, task => task.Id == "task_old");

        Assert.Equal("Legacy local task", task.Title);
        Assert.Equal(TaskItemStatus.None, task.Status);
        Assert.Equal(IntegrationIds.Local, task.IntegrationId);
    }

    private SqliteTaskStore CreateStore()
    {
        Directory.CreateDirectory(_directory);
        return new SqliteTaskStore(Path.Combine(_directory, "openza_tasks.db"));
    }

    public void Dispose()
    {
        TestDirectory.Delete(_directory);
    }
}
