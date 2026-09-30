// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mssql.McpServer.InsightsLayer;

/// <summary>
/// Queues post-write AI Insights DDL reconciliation work and runs it on a hosted background loop.
/// Work is tied to host lifetime cancellation so shutdown is graceful.
/// </summary>
public interface IInsightDdlProcessingQueue
{
    void RequestProcessing();
}

public sealed class InsightDdlProcessingQueue(
    IInsightsLayerService insightsLayer,
    ILogger<InsightDdlProcessingQueue> logger)
    : BackgroundService, IInsightDdlProcessingQueue
{
    private readonly IInsightsLayerService _insightsLayer = insightsLayer;
    private readonly ILogger<InsightDdlProcessingQueue> _logger = logger;

    // Coalesce bursts of write activity into one processing signal.
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    public void RequestProcessing()
    {
        if (!_insightsLayer.IsEnabled)
        {
            return;
        }

        _ = _signals.Writer.TryWrite(true);
    }

    internal static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(5);
    internal const int MaxConsecutiveBusyRetries = 3;

    // Touched only by the single ExecuteAsync reader loop.
    private int _consecutiveBusyRetries;

    private async Task RetryLaterAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(BusyRetryDelay, stoppingToken).ConfigureAwait(false);
            _ = _signals.Writer.TryWrite(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _signals.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                // Drain any accumulated signals before one reconciliation run.
                while (_signals.Reader.TryRead(out _))
                {
                }

                try
                {
                    if (await _insightsLayer.ProcessDdlChangesAsync(stoppingToken).ConfigureAwait(false))
                    {
                        _consecutiveBusyRetries = 0;
                    }
                    else if (_consecutiveBusyRetries < MaxConsecutiveBusyRetries)
                    {
                        // Another server process holds the lock; retry shortly so this write is not missed.
                        // Capped: the lock holder processes the same audit rows, so endless retries add nothing.
                        _consecutiveBusyRetries++;
                        _ = RetryLaterAsync(stoppingToken);
                    }
                }
                catch (Exception) when (stoppingToken.IsCancellationRequested)
                {
                    // SqlClient may surface a cancelled command as SqlException rather than OperationCanceledException.
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Post-write insight DDL processing failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
    }
}

public sealed class NoOpInsightDdlProcessingQueue : IInsightDdlProcessingQueue
{
    public static NoOpInsightDdlProcessingQueue Instance { get; } = new();

    private NoOpInsightDdlProcessingQueue()
    {
    }

    public void RequestProcessing()
    {
    }
}
