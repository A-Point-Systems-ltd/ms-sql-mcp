// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Mssql.McpServer.InsightsLayer;

namespace MssqlMcp.Tests;

/// <summary>
/// Pins the opt-OUT semantics of <c>USE_INSIGHTS_LAYER</c>: missing/empty/unknown values
/// must enable the layer, only explicit falsey tokens disable it. These tests mutate a
/// process-wide environment variable, so they run sequentially via <see cref="EnvVarLock"/>.
/// </summary>
[Collection(EnvVarLock.Name)]
public sealed class InsightsLayerEnvironmentTests
{
    private const string EnvVar = "USE_INSIGHTS_LAYER";
    private const string AutoPopulateVar = "INSIGHTS_AUTOPOPULATE";

    [Fact]
    public void Missing_variable_enables_layer()
    {
        using var _ = WithEnvVar(null);
        Assert.True(InsightsLayerEnvironment.IsInsightsLayerEnabled);
    }

    [Fact]
    public void Empty_variable_enables_layer()
    {
        using var _ = WithEnvVar(string.Empty);
        Assert.True(InsightsLayerEnvironment.IsInsightsLayerEnabled);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("anything")]
    [InlineData("   ")]
    public void Any_non_falsey_value_keeps_layer_enabled(string value)
    {
        using var _ = WithEnvVar(value);
        Assert.True(InsightsLayerEnvironment.IsInsightsLayerEnabled);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("False")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("off")]
    [InlineData("disabled")]
    [InlineData(" false ")]
    public void Falsey_tokens_disable_layer(string value)
    {
        using var _ = WithEnvVar(value);
        Assert.False(InsightsLayerEnvironment.IsInsightsLayerEnabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("custom")]
    public void AutoPopulate_defaults_enabled_unless_falsey(string? value)
    {
        using var _ = WithEnvVar(AutoPopulateVar, value);
        Assert.True(InsightsLayerEnvironment.IsAutoPopulationEnabled);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("disabled")]
    public void AutoPopulate_falsey_disables(string value)
    {
        using var _ = WithEnvVar(AutoPopulateVar, value);
        Assert.False(InsightsLayerEnvironment.IsAutoPopulationEnabled);
    }

    [Fact]
    public void Derived_auto_flags_follow_two_public_switches()
    {
        using var _ = new MultiEnvVarScope(new[]
        {
            (EnvVar, (string?)"true"),
            (AutoPopulateVar, (string?)"true")
        });
        Assert.True(InsightsLayerEnvironment.IsEnrichmentDirectiveEnabled);
        Assert.True(InsightsLayerEnvironment.IsAutoPopulationRowCountsEnabled);
        Assert.True(InsightsLayerEnvironment.IsAutoPopulationRefreshEnabled);
    }

    [Fact]
    public void Derived_auto_flags_disable_when_auto_population_is_off()
    {
        using var _ = new MultiEnvVarScope(new[]
        {
            (EnvVar, (string?)"true"),
            (AutoPopulateVar, (string?)"false")
        });
        Assert.False(InsightsLayerEnvironment.IsEnrichmentDirectiveEnabled);
        Assert.False(InsightsLayerEnvironment.IsAutoPopulationRowCountsEnabled);
        Assert.False(InsightsLayerEnvironment.IsAutoPopulationRefreshEnabled);
    }

    [Fact]
    public void Derived_auto_flags_disable_when_layer_is_off()
    {
        using var _ = new MultiEnvVarScope(new[]
        {
            (EnvVar, (string?)"false"),
            (AutoPopulateVar, (string?)"true")
        });
        Assert.False(InsightsLayerEnvironment.IsEnrichmentDirectiveEnabled);
        Assert.False(InsightsLayerEnvironment.IsAutoPopulationRowCountsEnabled);
        Assert.False(InsightsLayerEnvironment.IsAutoPopulationRefreshEnabled);
    }

    private static EnvVarScope WithEnvVar(string? value)
    {
        return new EnvVarScope(EnvVar, value);
    }

    private static EnvVarScope WithEnvVar(string variableName, string? value)
    {
        return new EnvVarScope(variableName, value);
    }

    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public EnvVarScope(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _original);
        }
    }

    private sealed class MultiEnvVarScope : IDisposable
    {
        private readonly Dictionary<string, string?> _original = new(StringComparer.OrdinalIgnoreCase);

        public MultiEnvVarScope(IEnumerable<(string name, string? value)> values)
        {
            foreach (var (name, value) in values)
            {
                _original[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var kv in _original)
            {
                Environment.SetEnvironmentVariable(kv.Key, kv.Value);
            }
        }
    }
}

[CollectionDefinition(EnvVarLock.Name, DisableParallelization = true)]
public sealed class EnvVarLockCollection
{
}

internal static class EnvVarLock
{
    public const string Name = "EnvironmentVariableLock";
}
