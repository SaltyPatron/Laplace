namespace Laplace.Pipeline;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Laplace.Pipeline.Abstractions;

using Npgsql;

/// <summary>
/// Generic Channel&lt;T&gt;-buffered binary-COPY sink. One bounded channel +
/// one long-lived Npgsql connection per record kind. The drain loop pulls
/// records from the channel in batches and pushes each batch through one
/// transactional COPY-into-pg_temp-staging + INSERT ... SELECT FROM
/// staging ON CONFLICT DO NOTHING cycle. Channel capacity gives natural
/// backpressure on producers.
///
/// Phase 2 / Track D / D5.
/// </summary>
public sealed class PgCopyBatchSink<T> : IBatchSink
{
    private readonly NpgsqlConnection                       _connection;
    private readonly string                                  _stagingDdl;
    private readonly string                                  _copyCommand;
    private readonly string                                  _insertSelect;
    private readonly Func<NpgsqlBinaryImporter, T, ValueTask> _writeRow;
    private readonly Channel<T>                              _channel;
    private readonly Task                                    _drainTask;
    private readonly CancellationTokenSource                 _drainCts;
    private readonly int                                     _batchSize;

    public PgCopyBatchSink(
        NpgsqlConnection                          connection,
        string                                    stagingDdl,
        string                                    copyCommand,
        string                                    insertSelect,
        Func<NpgsqlBinaryImporter, T, ValueTask>  writeRow,
        int                                       channelCapacity = 65_536,
        int                                       batchSize       = 4_096)
    {
        _connection   = connection;
        _stagingDdl   = stagingDdl;
        _copyCommand  = copyCommand;
        _insertSelect = insertSelect;
        _writeRow     = writeRow;
        _batchSize    = batchSize;
        _drainCts     = new CancellationTokenSource();
        _channel      = Channel.CreateBounded<T>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode     = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
        _drainTask = Task.Run(() => DrainLoopAsync(_drainCts.Token));
    }

    public async ValueTask EmitAsync(T record, CancellationToken cancellationToken)
    {
        await _channel.Writer.WriteAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        // Emit a sync token via the channel-completion mechanism: drain
        // anything currently queued. We cannot directly signal "process
        // exactly what's pending" without a custom marker; the simplest
        // semantics is: wait for the channel to be empty.
        while (_channel.Reader.Count > 0)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        await _drainTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try { await _drainTask.ConfigureAwait(false); } catch { /* swallow on shutdown */ }
        _drainCts.Dispose();
    }

    private async Task DrainLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new List<T>(_batchSize);

        // Ensure staging table exists for this connection's session.
        await using (var setupCmd = new NpgsqlCommand(_stagingDdl, _connection))
        {
            await setupCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await foreach (var record in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            buffer.Add(record);
            if (buffer.Count >= _batchSize)
            {
                await FlushBatchAsync(buffer, cancellationToken).ConfigureAwait(false);
                buffer.Clear();
            }
        }
        if (buffer.Count > 0)
        {
            await FlushBatchAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FlushBatchAsync(List<T> batch, CancellationToken cancellationToken)
    {
        await using var tx = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var truncate = new NpgsqlCommand("TRUNCATE pg_temp.laplace_staging", _connection, tx))
        {
            await truncate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var importer = await _connection.BeginBinaryImportAsync(_copyCommand, cancellationToken).ConfigureAwait(false))
        {
            foreach (var record in batch)
            {
                await importer.StartRowAsync(cancellationToken).ConfigureAwait(false);
                await _writeRow(importer, record).ConfigureAwait(false);
            }
            await importer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = new NpgsqlCommand(_insertSelect, _connection, tx))
        {
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
