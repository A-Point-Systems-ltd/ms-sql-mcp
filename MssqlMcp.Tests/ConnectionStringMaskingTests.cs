// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using Mssql.McpServer;

namespace MssqlMcp.Tests;

public sealed class ConnectionStringMaskingTests
{
    [Theory]
    [InlineData("Server=.;Database=x;User ID=u;Password=hunter2", "hunter2")]
    [InlineData("Server=.;Database=x;User ID=u;Pwd=hunter2", "hunter2")]
    [InlineData("Server=.;Database=x;User ID=u;Password=\"se;cr;et\"", "cr;et")]
    [InlineData("Server=.;Database=x;User ID=u;Password='a;b=c'", "b=c")]
    public void Mask_never_leaks_any_part_of_the_password(string connectionString, string secretFragment)
    {
        var masked = Program.MaskConnectionString(connectionString);

        Assert.DoesNotContain(secretFragment, masked, StringComparison.Ordinal);
        Assert.Contains("***MASKED***", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_keeps_non_secret_fields_for_diagnostics()
    {
        var masked = Program.MaskConnectionString("Server=DC\\DEV;Database=Sales;Trusted_Connection=True");

        Assert.Contains("DC\\DEV", masked, StringComparison.Ordinal);
        Assert.Contains("Sales", masked, StringComparison.Ordinal);
    }

    [Fact]
    public void Mask_does_not_echo_unparsable_input()
    {
        var masked = Program.MaskConnectionString("this is ; not = a valid ; Password=oops; UnknownKey=1");

        Assert.DoesNotContain("oops", masked, StringComparison.Ordinal);
    }
}