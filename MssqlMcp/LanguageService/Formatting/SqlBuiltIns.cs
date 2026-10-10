namespace Mssql.McpServer.LanguageService.Formatting;

/// <summary>Built-in T-SQL function names the formatter re-cases like keywords (user functions keep their case).</summary>
internal static class SqlBuiltIns
{
    private static readonly HashSet<string> Functions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Date and time
        "getdate", "getutcdate", "sysdatetime", "sysutcdatetime", "sysdatetimeoffset", "current_timestamp", "dateadd",
        "datediff", "datediff_big", "datename", "datepart", "datetrunc", "date_bucket", "day", "month", "year", "eomonth",
        "datefromparts", "datetimefromparts", "datetime2fromparts", "smalldatetimefromparts", "timefromparts",
        "datetimeoffsetfromparts", "isdate", "switchoffset", "todatetimeoffset",
        // String
        "ascii", "char", "charindex", "concat", "concat_ws", "difference", "format", "left", "len", "lower", "ltrim",
        "nchar", "patindex", "quotename", "replace", "replicate", "reverse", "right", "rtrim", "soundex", "space", "str",
        "string_agg", "string_escape", "string_split", "stuff", "substring", "translate", "trim", "unicode", "upper",
        "datalength",
        // Math
        "abs", "acos", "asin", "atan", "atn2", "ceiling", "cos", "cot", "degrees", "exp", "floor", "log", "log10", "pi",
        "power", "radians", "rand", "round", "sign", "sin", "sqrt", "square", "tan", "greatest", "least",
        // Aggregates and windows
        "avg", "checksum_agg", "count", "count_big", "grouping", "grouping_id", "max", "min", "stdev", "stdevp", "sum",
        "var", "varp", "approx_count_distinct", "row_number", "rank", "dense_rank", "ntile", "lag", "lead",
        "first_value", "last_value", "percent_rank", "cume_dist", "percentile_cont", "percentile_disc",
        // Conversion, logic, system
        "isnull", "isnumeric", "iif", "choose", "parse", "try_parse", "newid", "newsequentialid", "scope_identity",
        "ident_current", "ident_incr", "ident_seed", "object_id", "object_name", "object_schema_name", "object_definition",
        "objectproperty", "objectpropertyex", "schema_name", "schema_id", "db_name", "db_id", "type_name", "type_id",
        "col_name", "col_length", "columnproperty", "user_name", "user_id", "suser_name", "suser_sname", "suser_id",
        "host_name", "host_id", "app_name", "original_login", "session_user", "system_user", "is_member",
        "is_rolemember", "is_srvrolemember", "has_perms_by_name", "permissions", "serverproperty", "databaseproperty",
        "databasepropertyex", "error_message", "error_number", "error_line", "error_severity", "error_state",
        "error_procedure", "xact_state", "formatmessage", "checksum", "binary_checksum", "hashbytes", "compress",
        "decompress", "context_info", "session_context", "rowcount_big", "@@rowcount",
        // JSON
        "json_value", "json_query", "json_modify", "isjson", "json_object", "json_array", "json_path_exists",
        "openjson",
    };

    public static bool IsBuiltInFunction(string? name) => name is not null && Functions.Contains(name);
}
