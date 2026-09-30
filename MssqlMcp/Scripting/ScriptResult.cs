// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

namespace Mssql.McpServer.Scripting;

public enum DdlForm
{
    Create,
    CreateOrAlter,
    Alter,
}

public sealed record ScriptResult(string ObjectType, string? Schema, string Name, DdlForm Form, string Ddl, IReadOnlyList<string> Warnings);
