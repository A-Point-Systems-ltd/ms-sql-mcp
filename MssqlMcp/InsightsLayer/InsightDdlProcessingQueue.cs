// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mssql.McpServer.Connections;

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
    ILogger<InsightDdlProcessingQueue> logger,
    ConnectionRegistry registry)
    : BackgroundService, IInsightDdlProcessingQueue
{
    internal static readonly TimeSpan BusyRetryDelay = TimeSpan.FromSeconds(5);
    internal const int MaxConsecutiveBusyRetries = 3;

    // Unbounded, but _pending coalesces bursts to at most one queued signal per connection.
    private readonly Channel<string> _signals = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _busyRetries = new(StringComparer.OrdinalIgnoreCase);

    public void RequestProcessing()
    {
        if (!insightsLayer.IsEnabled)
        {
            return;
        }

        // Tool calls always run inside a CurrentConnection scope; outside one, only an unambiguous single connection qualifies.
        var name = CurrentConnection.Value?.Name ?? registry.TryResolveSingle()?.Name;
        if (name is not null && _pending.TryAdd(name, 0))
        {
            _ = _signals.Writer.TryWrite(name);
        }
    }

    private async Task RetryLaterAsync(string name, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(BusyRetryDelay, stoppingToken).ConfigureAwait(false);
            if (_pending.TryAdd(name, 0))
            {
                _ = _signals.Writer.TryWrite(name);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var name in _signals.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                _ = _pending.TryRemove(name, out _);
                var profile = registry.Find(name);
                // Read-only profiles never write, so their DDL processing (watermark, archive, baselines) is skipped too.
                if (profile is null || !registry.IsOpen(name) || !profile.InsightsEnabled || profile.ReadOnly)
                {
                    continue;
                }

                try
                {
                    bool processed;
                    using (CurrentConnection.Use(profile))
                    {
                        processed = await insightsLayer.ProcessDdlChangesAsync(stoppingToken).ConfigureAwait(false);
                    }

                    if (processed)
                    {
                        _ = _busyRetries.TryRemove(name, out _);
                    }
                    else if (_busyRetries.AddOrUpdate(name, 1, static (_, n) => n + 1) <= MaxConsecutiveBusyRetries)
                    {
                        // Another server process holds the lock; retry shortly so this write is not missed.
                        // Capped: the lock holder processes the same audit rows, so endless retries add nothing.
                        _ = RetryLaterAsync(name, stoppingToken);
                    }
                }
                catch (Exception) when (stoppingToken.IsCancellationRequested)
                {
                    // SqlClient may surface a cancelled command as SqlException rather than OperationCanceledException.
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Post-write insight DDL processing failed for connection {Connection}.", name);
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
