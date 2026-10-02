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

    [Theory]
    // 1. MSSQL_CONSOLE_LOG_LEVEL wins whenever it is set.
    [InlineData("Debug", "Error", "Critical", LogLevel.Debug)]
    [InlineData("none", null, "Information", LogLevel.None)]
    // 2. Otherwise the standard configuration: Logging:Console:LogLevel:Default before Logging:LogLevel:Default.
    [InlineData(null, "Information", "Error", LogLevel.Information)]
    [InlineData("", "Trace", null, LogLevel.Trace)]
    [InlineData(null, null, "Information", LogLevel.Information)]
    [InlineData("  ", " error ", null, LogLevel.Error)]
    [InlineData(null, "", "Debug", LogLevel.Debug)]
    // 3. Otherwise Warning.
    [InlineData(null, null, null, LogLevel.Warning)]
    [InlineData(null, "", "  ", LogLevel.Warning)]
    public void Precedence_is_variable_then_console_config_then_default_config_then_warning(
        string? variable, string? consoleDefault, string? loggingDefault, LogLevel expected)
    {
        Assert.Equal(expected, ConsoleLogLevel.Resolve(variable, consoleDefault, loggingDefault, out var warning));
        Assert.Null(warning);
    }

    [Fact]
    public void An_invalid_variable_warns_and_falls_through_to_the_configuration()
    {
        Assert.Equal(LogLevel.Information, ConsoleLogLevel.Resolve("verbose", null, "Information", out var warning));
        Assert.NotNull(warning);
        Assert.Contains(ConsoleLogLevel.Variable, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void An_invalid_configuration_level_is_skipped_and_numbers_are_read_like_microsoft_logging_reads_them()
    {
        Assert.Equal(LogLevel.Error, ConsoleLogLevel.Resolve(null, "loud", "Error", out var warning));
        Assert.Null(warning);
        Assert.Equal(LogLevel.Warning, ConsoleLogLevel.Resolve(null, "loud", "42", out _));
        Assert.Equal(LogLevel.Debug, ConsoleLogLevel.Resolve(null, "1", "Error", out _));
    }
}
