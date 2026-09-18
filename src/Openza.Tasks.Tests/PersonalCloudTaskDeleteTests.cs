using Microsoft.Data.Sqlite;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.Models;
using Openza.Tasks.Core.PersonalCloud;

namespace Openza.Tasks.Tests;

public sealed class PersonalCloudTaskDeleteTests : IDisposable
{
    private const string Instance="instance_00000000000000000000000000000001", Device="device_00000000000000000000000000000001", TaskId="local_00000000000000000000000000000001";
    private readonly string _directory=Path.Combine(Path.GetTempPath(),"openza-personal-cloud-delete-tests",Guid.NewGuid().ToString("N"));

    [Fact]
    public void Contract_is_strict_and_shared()
    {
        var json=Read("valid-task-delete.json");var mutation=PersonalCloudDeleteCodec.ParseMutation(json,Instance);
        Assert.Equal(8,mutation.BaseEntityVersion);Assert.Equal(Read("valid-task-delete.canonical.json").Trim(),PersonalCloudK0Codec.Canonicalize(json));
        Assert.IsType<PersonalCloudTaskDeletedChange>(Assert.Single(
            PersonalCloudK3Codec.ParseChangePage(Read("pulled-task-deleted-page.json")).Changes));
    }

    [Fact]
    public async Task Source_deletion_hides_task_but_retains_tombstone_and_rolls_back_with_outbox()
    {
        var store=Create("source.db");await store.InitializeAsync();
        await store.CreateTaskWithPersonalCloudIntentAsync(new TaskItem{Id=TaskId,SpaceId=SpaceIds.Default,Title="Delete me"},Instance,Device);
        await Execute(store.DatabasePath,"""
          CREATE TRIGGER fail_delete_outbox BEFORE INSERT ON personal_cloud_outbox
          WHEN NEW.operation='task.delete' BEGIN SELECT RAISE(ABORT,'fail'); END;
          """);
        await Assert.ThrowsAsync<SqliteException>(()=>store.DeleteTaskWithPersonalCloudIntentAsync(TaskId,DateTimeOffset.Parse("2026-09-13T06:46:00Z"),1,Instance,Device));
        Assert.NotNull(await store.GetTaskAsync(TaskId));Assert.Equal(0,await Scalar(store.DatabasePath,"SELECT COUNT(*) FROM personal_cloud_tombstones"));
        await Execute(store.DatabasePath,"DROP TRIGGER fail_delete_outbox");
        var deleted=await store.DeleteTaskWithPersonalCloudIntentAsync(TaskId,DateTimeOffset.Parse("2026-09-13T06:46:00Z"),1,Instance,Device);
        Assert.Equal(2,deleted.DeviceSequence);Assert.Null(await store.GetTaskAsync(TaskId));
        Assert.Equal(1,await Scalar(store.DatabasePath,"SELECT COUNT(*) FROM personal_cloud_tombstones WHERE purge_after IS NULL"));
    }

    private SqliteTaskStore Create(string name){Directory.CreateDirectory(_directory);return new(Path.Combine(_directory,name));}
    private static string Read(string name)=>File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","PersonalCloudK0",name));
    private static async Task Execute(string path,string sql){await using var c=Open(path);await c.OpenAsync();var x=c.CreateCommand();x.CommandText=sql;await x.ExecuteNonQueryAsync();}
    private static async Task<long> Scalar(string path,string sql){await using var c=Open(path);await c.OpenAsync();var x=c.CreateCommand();x.CommandText=sql;return Convert.ToInt64(await x.ExecuteScalarAsync());}
    private static SqliteConnection Open(string path)=>new(new SqliteConnectionStringBuilder{DataSource=path,Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString());
    public void Dispose()=>TestDirectory.Delete(_directory);
}
