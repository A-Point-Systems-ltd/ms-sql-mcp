# MS SQL MCP Tools Enhancement Plan

**Date**: 2025-10-26  
**Project**: MS SQL MCP Server  
**Status**: Approved - Ready for Implementation

## Executive Summary

This document outlines the plan to enhance the MS SQL MCP server with 11 new database introspection and management tools, plus enhancements to the existing [`DescribeTable`](../MssqlMcp/Tools/DescribeTable.cs:19) tool.

## Architecture Overview

The project follows a clean, modular architecture:
- **Partial class pattern**: Each tool is implemented as a partial method in [`Tools`](../MssqlMcp/Tools/Tools.cs:12) class in separate files
- **Attribute-based metadata**: Tools use `[McpServerTool]` and `[Description]` attributes
- **Consistent return type**: All tools return [`DbOperationResult`](../MssqlMcp/DbOperationResult.cs:9)
- **Dependency injection**: Uses [`ISqlConnectionFactory`](../MssqlMcp/ISqlConnectionFactory.cs:1) for connection management
- **Error handling**: Comprehensive try-catch with logging via `ILogger<Tools>`

## Tools Implementation Details

### 1. ListStoredProcedures

**File**: `MssqlMcp/Tools/ListStoredProcedures.cs`

**Purpose**: List all stored procedures with descriptions

**Attributes**:
- Title: "List Stored Procedures"
- ReadOnly: true
- Idempotent: true
- Destructive: false

**SQL Query**:
```sql
SELECT 
    s.name AS [schema],
    p.name AS name,
    p.object_id AS id,
    p.create_date,
    p.modify_date,
    ep.value AS description
FROM sys.procedures p
INNER JOIN sys.schemas s ON p.schema_id = s.schema_id
LEFT JOIN sys.extended_properties ep 
    ON ep.major_id = p.object_id 
    AND ep.minor_id = 0 
    AND ep.name = 'MS_Description'
ORDER BY s.name, p.name
```

**Return Structure**:
```json
{
  "success": true,
  "data": [
    {
      "schema": "dbo",
      "name": "usp_GetCustomers",
      "id": 123456,
      "create_date": "2024-01-01T00:00:00",
      "modify_date": "2024-01-01T00:00:00",
      "description": "Returns customer list"
    }
  ]
}
```

---

### 2. GetStoredProc

**File**: `MssqlMcp/Tools/GetStoredProc.cs`

**Purpose**: Get stored procedure details including parameters and code

**Parameters**:
- `name` (string): Stored procedure name, supports `schema.procname` format

**Attributes**:
- Title: "Get Stored Procedure"
- ReadOnly: true
- Idempotent: true
- Destructive: false

**SQL Queries**:

```sql
-- Procedure Info
SELECT 
    s.name AS [schema],
    p.name,
    p.create_date,
    p.modify_date,
    ep.value AS description
FROM sys.procedures p
INNER JOIN sys.schemas s ON p.schema_id = s.schema_id
LEFT JOIN sys.extended_properties ep 
    ON ep.major_id = p.object_id 
    AND ep.minor_id = 0 
    AND ep.name = 'MS_Description'
WHERE p.name = @ProcName 
    AND (s.name = @SchemaName OR @SchemaName IS NULL)

-- Parameters
SELECT 
    pm.name,
    t.name AS type,
    pm.max_length,
    pm.precision,
    pm.scale,
    pm.is_output,
    pm.has_default_value,
    pm.default_value
FROM sys.parameters pm
INNER JOIN sys.types t ON pm.user_type_id = t.user_type_id
WHERE pm.object_id = (
    SELECT p.object_id 
    FROM sys.procedures p
    INNER JOIN sys.schemas s ON p.schema_id = s.schema_id
    WHERE p.name = @ProcName 
        AND (s.name = @SchemaName OR @SchemaName IS NULL)
)
ORDER BY pm.parameter_id

-- Code
SELECT OBJECT_DEFINITION(p.object_id) AS definition
FROM sys.procedures p
INNER JOIN sys.schemas s ON p.schema_id = s.schema_id
WHERE p.name = @ProcName 
    AND (s.name = @SchemaName OR @SchemaName IS NULL)
```

**Implementation Notes**:
- Handle schema.name parsing like [`DescribeTable`](../MssqlMcp/Tools/DescribeTable.cs:23)
- Return error if procedure not found

**Return Structure**:
```json
{
  "success": true,
  "data": {
    "procedure": {
      "schema": "dbo",
      "name": "usp_GetCustomers",
      "create_date": "2024-01-01T00:00:00",
      "modify_date": "2024-01-01T00:00:00",
      "description": "Returns customer list"
    },
    "parameters": [
      {
        "name": "@CustomerId",
        "type": "int",
        "max_length": 4,
        "precision": 10,
        "scale": 0,
        "is_output": false,
        "has_default_value": false,
        "default_value": null
      }
    ],
    "definition": "CREATE PROCEDURE..."
  }
}
```

---

### 3. ListTableFunctions

**File**: `MssqlMcp/Tools/ListTableFunctions.cs`

**Purpose**: List all table-valued functions

**Attributes**:
- Title: "List Table Functions"
- ReadOnly: true
- Idempotent: true
- Destructive: false

**SQL Query**:
```sql
SELECT 
    s.name AS [schema],
    o.name,
    o.object_id AS id,
    o.create_date,
    o.modify_date,
    o.type_desc,
    ep.value AS description
FROM sys.objects o
INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
LEFT JOIN sys.extended_properties ep 
    ON ep.major_id = o.object_id 
    AND ep.minor_id = 0 
    AND ep.name = 'MS_Description'
WHERE o.type IN ('TF', 'IF', 'FT')  -- TF=Table Function, IF=Inline Table Function, FT=Assembly Table Function
ORDER BY s.name, o.name
```

**Implementation Notes**:
- `type_desc` distinguishes inline vs multi-statement table functions
- Types: TF (Multi-statement), IF (Inline), FT (Assembly)

---

### 4. ListScalarFunctions

**File**: `MssqlMcp/Tools/ListScalarFunctions.cs`

**Purpose**: List all scalar functions

**Attributes**:
- Title: "List Scalar Functions"
- ReadOnly: true
- Idempotent: true
- Destructive: false

**SQL Query**:
```sql
SELECT 
    s.name AS [schema],
    o.name,
    o.object_id AS id,
    o.create_date,
    o.modify_date,
    ep.value AS description
FROM sys.objects o
INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
LEFT JOIN sys.extended_properties ep 
    ON ep.major_id = o.object_id 
    AND ep.minor_id = 0 
    AND ep.name = 'MS_Description'
WHERE o.type = 'FN'  -- FN=Scalar Function
ORDER BY s.name, o.name
```

---

### 5. GetFunction

**File**: `MssqlMcp/Tools/GetFunction.cs`

**Purpose**: Get function details including parameters and code (works for all function types)

**Parameters**:
- `name` (string): Function name, supports `schema.functionname` format

**Attributes**:
- Title: "Get Function"
- ReadOnly: true
- Idempotent: true
- Destructive: false

**SQL Queries**:

```sql
-- Function Info
SELECT 
    s.name AS [schema],
    o.name,
    o.type_desc,
    o.create_date,
    o.modify_date,
    ep.value AS description
FROM sys.objects o
INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
LEFT JOIN sys.extended_properties ep 
    ON ep.major_id = o.object_id 
    AND ep.minor_id = 0 
    AND ep.name = 'MS_Description'
WHERE o.name = @FunctionName 
    AND (s.name = @SchemaName OR @SchemaName IS NULL)
    AND o.type IN ('FN', 'TF', 'IF', 'FT')

-- Parameters
SELECT 
    pm.name,
    t.name AS type,
    pm.max_length,
    pm.precision,
    pm.scale,
    pm.is_output
FROM sys.parameters pm
INNER JOIN sys.types t ON pm.user_type_id = t.user_type_id
WHERE pm.object_id = (
    SELECT o.object_id 
    FROM sys.objects o
    INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
    WHERE o.name = @FunctionName 
        AND (s.name = @SchemaName OR @SchemaName IS NULL)
        AND o.type IN ('FN', 'TF', 'IF', 'FT')
)
ORDER BY pm.parameter_id

-- Code
SELECT OBJECT_DEFINITION(o.object_id) AS definition
FROM sys.objects o
INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE o.name = @FunctionName 
    AND (s.name = @SchemaName OR @SchemaName IS NULL)
    AND o.type IN ('FN', 'TF', 'IF', 'FT')
```

**Implementation Notes**:
- Works for both scalar (FN) and table-valued functions (TF, IF, FT)
- Schema parsing same as other tools

---

### 6. ListViews

**File**: `MssqlMcp/Tools/ListViews.cs`

**Purpose**: List all views

**Attributes**:
- Title: "List Views"
- ReadOnly: true
- Idempotent: true
- Destructive: false

**SQL Query**:
```sql
SELECT 
    s.name AS [schema],
    v.name,
    v.object_id AS id,
    v.create_date,
    v.modify_date,
    ep.value AS description
FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
LEFT JOIN sys.extended_properties ep 
    ON ep.major_id = v.object_id 
    AND ep.minor_id = 0 
    AND ep.name = 'MS_Description'
ORDER BY s.name, v.name
```

**Implementation Notes**:
- Similar pattern to [`ListTables`](../MssqlMcp/Tools/ListTables.cs:21)

---

### 7. DescribeView

**File**: `MssqlMcp/Tools/DescribeView.cs`

**Purpose**: Get view definition including columns and SQL code

**Parameters**:
- `name` (string): View name, supports `schema.viewname` format

**Attributes**:
- Title: "Describe View"
- ReadOnly: true
- Idempotent: true
- Destructive: false

**SQL Queries**:

```sql
-- View Info
SELECT 
    s.name AS [schema],
    v.name,
    v.object_id AS id,
    v.create_date,
    v.modify_date,
    ep.value AS description
FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
LEFT JOIN sys.extended_properties ep 
    ON ep.major_id = v.object_id 
    AND ep.minor_id = 0 
    AND ep.name = 'MS_Description'
WHERE v.name = @ViewName 
    AND (s.name = @SchemaName OR @SchemaName IS NULL)

-- Columns
SELECT 
    c.name,
    ty.name AS type,
    c.max_length,
    c.precision,
    c.scale,
    c.is_nullable
FROM sys.columns c
INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
WHERE c.object_id = (
    SELECT v.object_id 
    FROM sys.views v
    INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
    WHERE v.name = @ViewName 
        AND (s.name = @SchemaName OR @SchemaName IS NULL)
)
ORDER BY c.column_id

-- Definition
SELECT OBJECT_DEFINITION(v.object_id) AS definition
FROM sys.views v
INNER JOIN sys.schemas s ON v.schema_id = s.schema_id
WHERE v.name = @ViewName 
    AND (s.name = @SchemaName OR @SchemaName IS NULL)
```

**Return Structure**:
```json
{
  "success": true,
  "data": {
    "view": {
      "schema": "dbo",
      "name": "vw_CustomerOrders",
      "id": 123456,
      "create_date": "2024-01-01T00:00:00",
      "modify_date": "2024-01-01T00:00:00",
      "description": "Customer orders view"
    },
    "columns": [...],
    "definition": "CREATE VIEW..."
  }
}
```

---

### 8. ListTableTriggers

**File**: `MssqlMcp/Tools/ListTableTriggers.cs`

**Purpose**: List all table triggers

**Attributes**:
- Title: "List Table Triggers"
- ReadOnly: true
- Idempotent: true
- Destructive: false

**SQL Query**:
```sql
SELECT 
    s.name AS [schema],
    OBJECT_NAME(tr.parent_id) AS table_name,
    tr.name,
    tr.object_id AS id,
    tr.create_date,
    tr.modify_date,
    tr.is_disabled,
    tr.is_instead_of_trigger,
    ep.value AS description,
    STRING_AGG(te.type_desc, ', ') AS trigger_events
FROM sys.triggers tr
INNER JOIN sys.objects o ON tr.parent_id = o.object_id
INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
LEFT JOIN sys.extended_properties ep 
    ON ep.major_id = tr.object_id 
    AND ep.minor_id = 0 
    AND ep.name = 'MS_Description'
LEFT JOIN sys.trigger_events te ON tr.object_id = te.object_id
WHERE tr.parent_class = 1  -- Object or column triggers
GROUP BY s.name, tr.parent_id, tr.name, tr.object_id, 
         tr.create_date, tr.modify_date, tr.is_disabled, 
         tr.is_instead_of_trigger, ep.value
ORDER BY s.name, OBJECT_NAME(tr.parent_id), tr.name
```

**Implementation Notes**:
- Shows trigger events (INSERT, UPDATE, DELETE)
- Shows disabled status
- Shows INSTEAD OF vs AFTER trigger type

**Return Structure**:
```json
{
  "success": true,
  "data": [
    {
      "schema": "dbo",
      "table_name": "Customers",
      "name": "tr_Customer_Insert",
      "id": 123456,
      "create_date": "2024-01-01T00:00:00",
      "modify_date": "2024-01-01T00:00:00",
      "is_disabled": false,
      "is_instead_of_trigger": false,
      "description": "Audit trigger",
      "trigger_events": "INSERT, UPDATE"
    }
  ]
}
```

---

### 9. GetTrigger

**File**: `MssqlMcp/Tools/GetTrigger.cs`

**Purpose**: Get trigger details and SQL code

**Parameters**:
- `name` (string): Trigger name

**Attributes**:
- Title: "Get Trigger"
- ReadOnly: true
- Idempotent: true
- Destructive: false

**SQL Queries**:

```sql
-- Trigger Info
SELECT 
    s.name AS [schema],
    OBJECT_NAME(tr.parent_id) AS table_name,
    tr.name,
    tr.create_date,
    tr.modify_date,
    tr.is_disabled,
    tr.is_instead_of_trigger,
    ep.value AS description,
    STRING_AGG(te.type_desc, ', ') AS trigger_events
FROM sys.triggers tr
INNER JOIN sys.objects o ON tr.parent_id = o.object_id
INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
LEFT JOIN sys.extended_properties ep 
    ON ep.major_id = tr.object_id 
    AND ep.minor_id = 0 
    AND ep.name = 'MS_Description'
LEFT JOIN sys.trigger_events te ON tr.object_id = te.object_id
WHERE tr.name = @TriggerName 
    AND tr.parent_class = 1
GROUP BY s.name, tr.parent_id, tr.name, tr.create_date, 
         tr.modify_date, tr.is_disabled, tr.is_instead_of_trigger, ep.value

-- Definition
SELECT OBJECT_DEFINITION(tr.object_id) AS definition
FROM sys.triggers tr
WHERE tr.name = @TriggerName
```

**Return Structure**:
```json
{
  "success": true,
  "data": {
    "trigger": {
      "schema": "dbo",
      "table_name": "Customers",
      "name": "tr_Customer_Insert",
      "create_date": "2024-01-01T00:00:00",
      "modify_date": "2024-01-01T00:00:00",
      "is_disabled": false,
      "is_instead_of_trigger": false,
      "description": "Audit trigger",
      "trigger_events": "INSERT, UPDATE"
    },
    "definition": "CREATE TRIGGER..."
  }
}
```

---

### 10. ListSysObjects

**File**: `MssqlMcp/Tools/ListSysObjects.cs`

**Purpose**: Return sys.objects table data with optional filtering

**Parameters**:
- `type` (string, optional): Object type filter (e.g., 'U' for tables, 'P' for procs, 'V' for views)

**Attributes**:
- Title: "List System Objects"
- ReadOnly: true
- Idempotent: true
- Destructive: false

**SQL Query**:
```sql
SELECT 
    o.object_id,
    s.name AS [schema],
    o.name,
    o.type,
    o.type_desc,
    o.create_date,
    o.modify_date,
    o.is_ms_shipped,
    ep.value AS description
FROM sys.objects o
INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
LEFT JOIN sys.extended_properties ep 
    ON ep.major_id = o.object_id 
    AND ep.minor_id = 0 
    AND ep.name = 'MS_Description'
WHERE (@Type IS NULL OR o.type = @Type)
    AND o.is_ms_shipped = 0  -- Exclude system objects
ORDER BY s.name, o.name
```

**Implementation Notes**:
- Filter out system objects by default
- Optional type parameter for filtering

**Common Object Types**:
- U = User table
- P = Stored procedure
- V = View
- FN = Scalar function
- TF = Table-valued function
- IF = Inline table function
- TR = Trigger

---

### 11. ExecuteSQL

**File**: `MssqlMcp/Tools/ExecuteSQL.cs`

**Purpose**: Execute custom SQL commands (DDL, DML, queries)

**Parameters**:
- `sql` (string): SQL command to execute

**Attributes**:
- Title: "Execute SQL"
- **ReadOnly: false**
- **Idempotent: false**
- **Destructive: true**

**Implementation Pattern**:
```csharp
// Detect if it's a SELECT query
bool isSelect = sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);

if (isSelect)
{
    // Use ExecuteReaderAsync - similar to ReadData
    using var reader = await cmd.ExecuteReaderAsync();
    var results = new List<Dictionary<string, object?>>();
    while (await reader.ReadAsync())
    {
        var row = new Dictionary<string, object?>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }
        results.Add(row);
    }
    return new DbOperationResult(success: true, data: results);
}
else
{
    // Use ExecuteNonQueryAsync for INSERT, UPDATE, DELETE, DDL
    int rowsAffected = await cmd.ExecuteNonQueryAsync();
    return new DbOperationResult(success: true, rowsAffected: rowsAffected);
}
```

**Security Considerations**:
- This tool can modify data and schema
- User must have appropriate permissions
- No SQL injection protection beyond parameterization (not applicable for freeform SQL)
- Consider adding audit logging for this tool

---

### 12. DescribeTable Enhancement

**File**: `MssqlMcp/Tools/DescribeTable.cs` (modify existing)

**Purpose**: Add triggers information to existing table description

**Current Features** (already implemented in [`DescribeTable`](../MssqlMcp/Tools/DescribeTable.cs:19)):
- Table metadata
- Columns
- Indexes
- Constraints
- Foreign keys

**New Feature to Add**: Triggers

**Additional SQL Query**:
```sql
-- Triggers (ADD THIS SECTION)
SELECT 
    tr.name,
    tr.is_disabled,
    tr.is_instead_of_trigger,
    STRING_AGG(te.type_desc, ', ') AS trigger_events,
    ep.value AS description
FROM sys.triggers tr
LEFT JOIN sys.extended_properties ep 
    ON ep.major_id = tr.object_id 
    AND ep.minor_id = 0 
    AND ep.name = 'MS_Description'
LEFT JOIN sys.trigger_events te ON tr.object_id = te.object_id
WHERE tr.parent_id = (
    SELECT t.object_id 
    FROM sys.tables t
    INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
    WHERE t.name = @TableName 
        AND (s.name = @TableSchema OR @TableSchema IS NULL)
)
GROUP BY tr.name, tr.is_disabled, tr.is_instead_of_trigger, ep.value
```

**Implementation Location**:
- Add after the Foreign Keys section (around line 202)
- Add new result field: `result["triggers"] = triggers;`

**Updated Return Structure**:
```json
{
  "success": true,
  "data": {
    "table": {...},
    "columns": [...],
    "indexes": [...],
    "constraints": [...],
    "foreignKeys": [...],
    "triggers": [
      {
        "name": "tr_Customer_Insert",
        "is_disabled": false,
        "is_instead_of_trigger": false,
        "trigger_events": "INSERT, UPDATE",
        "description": "Audit trigger"
      }
    ]
  }
}
```

---

## Implementation Checklist

### Phase 1: List Tools (Read-Only)
- [ ] ListStoredProcedures.cs
- [ ] ListTableFunctions.cs
- [ ] ListScalarFunctions.cs
- [ ] ListViews.cs
- [ ] ListTableTriggers.cs
- [ ] ListSysObjects.cs

### Phase 2: Detail Tools (Read-Only)
- [ ] GetStoredProc.cs
- [ ] GetFunction.cs
- [ ] DescribeView.cs
- [ ] GetTrigger.cs

### Phase 3: Write Tool
- [ ] ExecuteSQL.cs

### Phase 4: Enhancement
- [ ] Enhance DescribeTable.cs with triggers

### Phase 5: Testing
- [ ] Test all List* tools
- [ ] Test all Get*/Describe* tools
- [ ] Test ExecuteSQL with various SQL types
- [ ] Test enhanced DescribeTable
- [ ] Test error handling and edge cases
- [ ] Test schema-qualified object names

---

## File Structure After Implementation

```
MssqlMcp/
├── DbOperationResult.cs
├── ISqlConnectionFactory.cs
├── MssqlMcp.csproj
├── Program.cs
├── SqlConnectionFactory.cs
└── Tools/
    ├── CreateTable.cs (existing)
    ├── DescribeTable.cs (modify - add triggers)
    ├── DescribeView.cs (new)
    ├── DropTable.cs (existing)
    ├── ExecuteSQL.cs (new)
    ├── GetFunction.cs (new)
    ├── GetStoredProc.cs (new)
    ├── GetTrigger.cs (new)
    ├── InsertData.cs (existing)
    ├── ListScalarFunctions.cs (new)
    ├── ListStoredProcedures.cs (new)
    ├── ListSysObjects.cs (new)
    ├── ListTableFunctions.cs (new)
    ├── ListTableTriggers.cs (new)
    ├── ListTables.cs (existing)
    ├── ListViews.cs (new)
    ├── ReadData.cs (existing)
    ├── Tools.cs (existing - partial class base)
    └── UpdateData.cs (existing)
```

---

## Key Implementation Guidelines

### 1. Schema Handling
All tools that accept object names should support `schema.objectname` format:
```csharp
string? schema = null;
if (name.Contains('.'))
{
    var parts = name.Split('.');
    if (parts.Length > 1)
    {
        schema = parts[0];
        name = parts[1];
    }
}
```

### 2. Error Handling Pattern
```csharp
var conn = await _connectionFactory.GetOpenConnectionAsync();
try
{
    using (conn)
    {
        // Implementation
        return new DbOperationResult(success: true, data: result);
    }
}
catch (Exception ex)
{
    _logger.LogError(ex, "ToolName failed: {Message}", ex.Message);
    return new DbOperationResult(success: false, error: ex.Message);
}
```

### 3. Null Safety
- Use `DBNull.Value` for null parameter values
- Check `reader["column"] is DBNull` before accessing values
- Use nullable types (`string?`, `object?`) appropriately

### 4. SQL Injection Prevention
- Always use parameterized queries with `@ParameterName`
- Use `cmd.Parameters.AddWithValue()`
- Exception: ExecuteSQL (by design accepts freeform SQL)

### 5. Extended Properties
Include MS_Description for documentation:
```sql
LEFT JOIN sys.extended_properties ep 
    ON ep.major_id = o.object_id 
    AND ep.minor_id = 0 
    AND ep.name = 'MS_Description'
```

### 6. Attribute Pattern
```csharp
[McpServerTool(
    Title = "Tool Name",
    ReadOnly = true,
    Idempotent = true,
    Destructive = false),
    Description("Tool description")]
public async Task<DbOperationResult> ToolName(
    [Description("Parameter description")] string parameter)
```

---

## Risk Assessment

| Tool | Risk Level | Reason |
|------|-----------|---------|
| All List* tools | Low | Read-only, no data modification |
| All Get*/Describe* tools | Low | Read-only, no data modification |
| ExecuteSQL | **High** | Can modify/delete data and schema |
| DescribeTable enhancement | Medium | Modifying existing functionality |

---

## Testing Strategy

### Unit Tests
- Test each tool with valid inputs
- Test schema-qualified names
- Test non-existent objects (error handling)
- Test null/empty inputs

### Integration Tests
- Test with actual SQL Server database
- Test with various object types
- Test ExecuteSQL with different SQL statement types
- Test enhanced DescribeTable includes triggers

### Edge Cases
- Objects with special characters in names
- Objects in non-dbo schemas
- System objects (should be filtered in ListSysObjects)
- Disabled triggers
- Functions without parameters

---

## Success Criteria

- [ ] All 11 new tools implemented and functional
- [ ] DescribeTable enhanced with triggers information
- [ ] All tools follow consistent patterns
- [ ] Comprehensive error handling
- [ ] All tools properly documented
- [ ] Unit tests passing
- [ ] Integration tests passing
- [ ] Code review completed
- [ ] Documentation updated

---

## Next Steps

1. Switch to Code mode for implementation
2. Implement tools in phases (List, Detail, Write, Enhancement)
3. Test each phase before proceeding
4. Update README.md with new tool documentation
5. Create unit tests for new tools

---

## References

- Existing [`Tools`](../MssqlMcp/Tools/Tools.cs:12) class base
- [`DescribeTable`](../MssqlMcp/Tools/DescribeTable.cs:19) implementation pattern
- [`ListTables`](../MssqlMcp/Tools/ListTables.cs:21) simple list pattern
- [`ReadData`](../MssqlMcp/Tools/ReadData.cs:18) query execution pattern
- [`DbOperationResult`](../MssqlMcp/DbOperationResult.cs:9) return type