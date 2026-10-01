// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Microsoft.Extensions.Logging;
using Mssql.McpServer;

namespace MssqlMcp.Tests;

/// <summary>MSSQL_CONSOLE_LOG_LEVEL parsing: stderr is quiet (Warning) unless the variable names a level.</summary>
public sealed class ConsoleLogLevelTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unset_defaults_to_warning_without_a_warning(string? value)
    {
        Assert.Equal(LogLevel.Warning, ConsoleLogLevel.Resolve(value, out var warning));
        Assert.Null(warning);
    }

    [Theory]
    [InlineData("Trace", LogLevel.Trace)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("INFORMATION", LogLevel.Information)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("Critical", LogLevel.Critical)]
    [InlineData(" none ", LogLevel.None)]
    public void Level_names_are_case_insensitive(string value, LogLevel expected)
    {
        Assert.Equal(expected, ConsoleLogLevel.Resolve(value, out var warning));
        Assert.Null(warning);
    }

    [Theory]
    [InlineData("verbose")]
    [InlineData("2")]
    [InlineData("Info")]
    public void Invalid_value_falls_back_to_warning_with_one_warning_line(string value)
    {
        Assert.Equal(LogLevel.Warning, ConsoleLogLevel.Resolve(value, out var warning));
        Assert.NotNull(warning);
        Assert.DoesNotContain('\n', warning);
        Assert.Contains(ConsoleLogLevel.Variable, warning, StringComparison.Ordinal);
    }
}
