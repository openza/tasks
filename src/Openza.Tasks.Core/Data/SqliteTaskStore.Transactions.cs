using Microsoft.Data.Sqlite;

namespace Openza.Tasks.Core.Data;

public sealed partial class SqliteTaskStore
{
    private readonly AsyncLocal<SqliteConnection?> _transactionConnection = new();

    public async Task<T> ExecuteInTransactionAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var previous = _transactionConnection.Value;
        _transactionConnection.Value = connection;
        try
        {
            var result = await action().ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            _transactionConnection.Value = previous;
        }
    }

    private sealed class ConnectionLease(SqliteConnection connection, bool ownsConnection) : IAsyncDisposable
    {
        public static implicit operator SqliteConnection(ConnectionLease lease) => lease.Connection;
        private SqliteConnection Connection { get; } = connection;

        public SqliteCommand CreateCommand() => Connection.CreateCommand();
        public Task CloseAsync() => ownsConnection ? Connection.CloseAsync() : Task.CompletedTask;
        public ValueTask DisposeAsync() => ownsConnection ? Connection.DisposeAsync() : ValueTask.CompletedTask;

        public async ValueTask<TransactionLease> BeginTransactionAsync(CancellationToken cancellationToken)
        {
            if (ownsConnection)
                return new TransactionLease(Connection, (SqliteTransaction)await Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false), null);

            var savepoint = $"nested_{Guid.NewGuid():N}";
            await using var command = Connection.CreateCommand();
            command.CommandText = $"SAVEPOINT {savepoint}";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new TransactionLease(Connection, null, savepoint);
        }
    }

    private sealed class TransactionLease(SqliteConnection connection, SqliteTransaction? transaction, string? savepoint) : IAsyncDisposable
    {
        private bool _completed;

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            else await ExecuteAsync($"RELEASE SAVEPOINT {savepoint}", cancellationToken).ConfigureAwait(false);
            _completed = true;
        }

        public async Task RollbackAsync(CancellationToken cancellationToken)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            else await ExecuteAsync($"ROLLBACK TO SAVEPOINT {savepoint}; RELEASE SAVEPOINT {savepoint}", cancellationToken).ConfigureAwait(false);
            _completed = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed) await RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            if (transaction is not null) await transaction.DisposeAsync().ConfigureAwait(false);
        }

        private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
