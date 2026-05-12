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

    private static EnvVarScope WithEnvVar(string? value)
    {
        return new EnvVarScope(EnvVar, value);
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
}

[CollectionDefinition(EnvVarLock.Name, DisableParallelization = true)]
public sealed class EnvVarLockCollection
{
}

internal static class EnvVarLock
{
    public const string Name = "EnvironmentVariableLock";
}
