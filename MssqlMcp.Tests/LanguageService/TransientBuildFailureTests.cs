using Mssql.McpServer.LanguageService;

namespace MssqlMcp.Tests.LanguageService;

/// <summary>Which metadata-build failures are retried: only the explicit transient set, found anywhere in the inner chain.</summary>
public sealed class TransientBuildFailureTests
{
    /// <summary>Stands in for a SqlException (no public constructor): the number function below reads it.</summary>
    private sealed class FakeSqlError(int number, Exception? inner = null) : Exception("sql error", inner)
    {
        public int Number { get; } = number;
    }

    private static int? NumberOf(Exception ex) => ex is FakeSqlError f ? f.Number : null;

    [Theory]
    [InlineData(596)]
    [InlineData(233)]
    [InlineData(10053)]
    [InlineData(10054)]
    [InlineData(64)]
    public void Transient_numbers_are_retried_directly_and_when_wrapped(int number)
    {
        Assert.Equal(number, LanguageServiceCache.TransientSqlNumber(new FakeSqlError(number), NumberOf));
        Assert.Equal(number, LanguageServiceCache.TransientSqlNumber(
            new InvalidOperationException("smo", new ApplicationException("wrapped", new FakeSqlError(number))), NumberOf));
    }

    [Theory]
    [InlineData(18456)] // login failed
    [InlineData(18486)] // login locked out
    [InlineData(4060)]  // cannot open database
    [InlineData(229)]   // permission denied
    [InlineData(208)]   // invalid object name
    public void Login_permission_and_database_errors_are_not_retried(int number)
    {
        Assert.Null(LanguageServiceCache.TransientSqlNumber(new FakeSqlError(number), NumberOf));
        Assert.Null(LanguageServiceCache.TransientSqlNumber(new InvalidOperationException("smo", new FakeSqlError(number)), NumberOf));
    }

    [Fact]
    public void Non_sql_exceptions_are_not_retried() =>
        Assert.Null(LanguageServiceCache.TransientSqlNumber(new TimeoutException("t", new IOException("io")), NumberOf));

    [Fact]
    public void The_production_classifier_ignores_non_sql_exceptions() =>
        Assert.Null(LanguageServiceCache.TransientSqlNumber(new InvalidOperationException("x")));
}
