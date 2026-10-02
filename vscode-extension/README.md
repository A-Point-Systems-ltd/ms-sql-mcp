# APoint-ms-sql

SQL Server for AI agents in VS Code, Cursor and Claude. The extension bundles the APoint-ms-sql server (a .NET 10 single-file exe) and gives you:

- **Connection form**: one form to add or edit a named connection (Windows, SQL login or Microsoft Entra authentication), with **Test connection** and **List databases**. Passwords are kept in VS Code SecretStorage only.
- **Object explorer**: tables, views, procedures, functions, triggers, types and security objects per connection, with a name filter and a data view.
- **DDL scripts**: SSMS-style scripts for every object. Tables, indexes, foreign keys, triggers, types and security objects open read-only.
- **Editing views, procedures and functions**: their DDL opens as an editable document bound to its connection; **Run** (F5) applies it to the database.
- **Query windows**: New Query on a connection, run with F5 (the selection or the whole document) and cancel.
- **Results panel**: one grid per result set and a Messages tab, kept on your machine.
- **Data grid** (Data View and Results): one-line rows, resizable and auto-fitting columns and rows, a copy button on every cell, and column sort.
- **Agent server and registration**: VS Code agent mode and Cursor get the MCP server automatically; Cursor CLI, Claude Desktop and Claude Code with one command.
- **Read-only semantics**: a read-only connection is enforced by the server for agents, the explorer and query windows alike. An optional **AI Insights** layer caches what agents learn about your schema.

> **Windows x64 only.** The package is built for `win32-x64`; the bundled server is a Windows executable.

## Features

### Object explorer

The **APoint-ms-sql** view in the activity bar lists your connections. Each open connection shows:

```text
<connection>
├─ Tables
│   └─ <table>  ─ Indexes, Foreign Keys, Triggers
├─ Views
│   └─ <view>   ─ Indexes
├─ Stored Procedures
├─ Table-Valued Functions
├─ Scalar Functions
├─ Database Triggers
├─ Types
└─ Security
    ├─ Logins
    ├─ Server Roles
    ├─ Database Users
    └─ Database Roles
```

- **Show DDL** (inline icon, or a click on the object) opens the object's script. Views, stored procedures and functions open as an editable document (see [Editing views, procedures and functions](#editing-views-procedures-and-functions)); everything else opens in a read-only editor. Scripts are written from the catalog views, SSMS style: `CREATE TABLE` with keys, defaults, checks, indexes, foreign keys and the description; `CREATE OR ALTER` (SQL Server 2016 SP1+) or `ALTER` for programmable objects. Anything the script cannot express is listed as `-- WARNING:` lines at the top. Passwords, password hashes and SIDs are never scripted: SQL logins and application roles get the placeholder `N'<password not scripted - set before running>'`.
- **Data View** (inline icon on tables and views) shows the first rows in a grid; **Load more** appends the next n rows with the same filters and sort, up to 10000 rows in all (then "Refine the filter to see more."). Pages are numbered with `ROW_NUMBER()` (SQL Server 2008 R2 and later) over the sort column and the table's primary key (read once per table through the read-only explorer); without a primary key the view notes "Without a primary key, rows with equal sort values may repeat or be skipped between pages." (or, with no sort either, "Row order is not guaranteed without a primary key or sort; pages may overlap."). When a sort, filter or Reload fails, the rows already shown stay with the sort, filters and TOP they were loaded with, and the error is shown above them; Load more continues with those. Reload also reads the primary key again. It runs one fixed, generated `SELECT TOP (n) * FROM [schema].[name]` (plus `ORDER BY [column]` when sorted) through the query runner (`run_script`), under the connection profile's own read-only flag, not the explorer's forced read-only process; a read-only profile allows this single SELECT. The column names and types show even for an empty table. n starts at the `msSqlMcp.dataViewRows` setting (default 200, max 10000); change it in the **TOP** box and press **Reload** or Enter (an invalid value shows an error and runs nothing). When there are more rows, the view says "first n rows (truncated)". Click a column header to sort on the server (ascending, descending, then unsorted; ▲ / ▼ shows the direction); TOP and Reload keep the sort; if the sorted column no longer exists after a reload, the sort is cleared and the panel says "Sort column no longer exists". Columns of type `text`, `ntext`, `image`, `xml`, `geography`, `geometry`, `hierarchyid`, `sql_variant` and CLR types cannot be sorted (their header tooltip says so). The panel is reused: it keeps the object, TOP and sort until you open Data View on another object. Errors (for example a closed connection) show in the panel. The SQL contains only the bracket-quoted object and column names and the TOP number, never typed text.
- **The data grid** (Data View and the Results panel):
  - Each row is one line of text; long values end in "…" and the full value (up to 2000 characters) is in the cell's tooltip. NULL is shown in italics, numeric columns are right-aligned.
  - Drag the right edge of a column header to resize the column (at least 40 px); double-click it to fit the column to its widest value (up to 600 px). On load, columns are fitted to their first 200 rows, up to 300 px.
  - Drag the bottom edge of a row number to resize the row; double-click it to fit the row to its content (wrapped, up to 400 px; taller cells scroll), and double-click again to return it to one line. Sizes reset when new data loads.
  - Hover a cell for its **copy** button: it copies that one value to the clipboard (NULL as empty text, binary as its `0x…` text, long values in full as loaded) and the status bar shows "Copied", or "Copied (value truncated by the server)" for a value the server cut at its cell cap. A second hover icon, **Open in viewer** (or a double-click on the cell), opens the full value in a read-only tab titled `<column> · row <n> - <object>`: JSON objects and arrays pretty-printed, XML indented, anything else as plain text (binary as `0x…`). The text is kept in memory only and dropped when the tab is closed.
  - **Toolbar** (one per grid): **Columns** (a checklist to hide and show columns, with **Show all**), **Wrap** (all rows wrapped, with the same caps as the row auto-fit; a row's double-click still overrides it), **Stripes** (alternate row background, on by default), a **Search** box (highlights matching cells in the loaded, visible rows, case-insensitive; "k matches", with previous / next and Enter / Shift+Enter), and **CSV** (Export). Data View adds **TOP**, **Reload** and **Load more**.
  - **Columns**: the row-number gutter and the first column stay in place when scrolling sideways; right-click a header for **Freeze up to here**, **Unfreeze columns** and **Hide column**. Drag a header onto another to reorder the columns (the view only). Copy and Export use the visible columns in the displayed order. The column layout is kept while you reload, sort, filter or load more in the same Data View, and resets for another object or another query.
  - **Filter row** under the header: one box per column with an operator (`contains`, `=`, `starts with`, `is null`, `is not null`). Data View filters on the server: the value is applied after a short pause or with Enter, and the query runs again with the filters and the current sort, built as `CAST([column] AS NVARCHAR(MAX)) LIKE N'%…%' ESCAPE N'\'` (with `%`, `_`, `[` and `\` escaped), `= N'…'` or `IS [NOT] NULL`. Only the bracket-quoted column name and a quoted string literal reach the SQL. Binary, xml, spatial, `hierarchyid`, `sql_variant` and CLR columns offer only is null / is not null. Comparisons use the column's text form from SQL Server's `CAST(... AS NVARCHAR(MAX))`, which can differ from what the grid shows: dates use SQL Server's default text form (for example `Oct  2 2026 10:00AM` for `datetime`), and `float` / `real` values are written in scientific notation (for example `1.5e+006`), so `contains` on them matches that text. In the Results panel the same operators filter the loaded rows locally (case-insensitive), and the caption says "filtered locally".
  - **Selection**: click and drag, or Shift+click, to select a block of cells (a row number selects the row); **Ctrl+A** selects all loaded rows. **Ctrl+C**, or **Copy** in the right-click menu, copies the block as tab-separated text with a header row. The status line shows the count and, for numeric columns, the sum, min, max and average (exact decimal arithmetic for `decimal` / `money` / `bigint` values).
  - **Copy row**: the row-number gutter's hover button (or right-click) offers **Copy row (tab-separated)** and **Copy row (JSON)** (column names as keys, NULL as `null`; tabs and line breaks become spaces in the tab-separated form).
  - **Export CSV** asks first, every time: "Export N rows to a CSV file? The data may contain personal information; keep the file inside the company." Then you choose the file (default `<object or results>_<yyyyMMdd_HHmm>.csv`). The file is UTF-8 with a byte order mark (so Excel shows Hebrew correctly), RFC 4180 quoted, with the visible columns in the displayed order and every loaded row that passes the filters. In text columns, a value that starts with `=`, `+`, `-`, `@`, a tab or a carriage return is written as `'` + the value (quoted), so Excel shows it as text instead of running it as a formula (the confirmation says so); numeric, date and binary columns are written as they are. The `msSqlMcp.export.neutralizeFormulas` setting (default on) turns this off.
  - Copied text (a cell, a row, a selection) is put on the clipboard exactly as it is, without that formula prefix: check what you paste into a spreadsheet.
- **Filter** shows only objects whose name contains the text you type (case-insensitive).
- Closed connections stay in the list; right-click **Open** to browse them again.

### MCP server for agents

VS Code agent mode lists **APoint-ms-sql** as an MCP server automatically. It serves every **open** connection (23 tools: inspection, `read_data`, `execute_sql`, `script_object`, insights, connection management).

**The multi-connection rule.** With exactly one open connection, agents may omit the `connection` argument. With more than one, every tool call **must** name its connection: there is no default connection. The server tells agents this in its instructions, in each tool's description and in `list_connections`, and an unnamed call returns an error that lists the valid names.

When you add, edit, open or close a connection, or change a setting that affects the server, the server definition gets a new version, and VS Code should prompt you to restart the server so agents see the new set. If it does not, restart **APoint-ms-sql** from the MCP server list. A changed password alone does not change the version; restart the server after changing one.

## Setup

1. Install the extension and open the **APoint-ms-sql** view.
2. Click **Add Connection** (`+`). One form opens with every setting; to change a connection later, use **Edit Connection** (same form, name read-only):
   - **Name**: letters, digits, `-`, `_`, `.`; unique, case-insensitive.
   - **Authentication**: Windows integrated (default), SQL login (user + password), Microsoft Entra interactive, Microsoft Entra default credential, or a raw connection string (without a password; shown as a text area, and Server, Database and Encryption are hidden).
   - **Server** (`host`, `host\instance` or `host,port`) and **Database**. **List databases** connects to the server (database `master`) with the values typed so far and fills the Database suggestions.
   - **User** (SQL login and Entra interactive) and **Password** (SQL login). The password field is never pre-filled; when editing a connection that has a saved password, leave it empty to keep it.
   - **Encryption** and **Trust server certificate**. Defaults: Mandatory + trust (self-signed / on-prem friendly):

     | Option | Use it when |
     |--------|-------------|
     | Mandatory, trust off | The server certificate is trusted by this machine. |
     | Mandatory, trust on | Self-signed / on-prem certificates on a trusted network (encrypted, but not protected against a man-in-the-middle). |
     | Optional (no encryption) | Legacy servers only; traffic, including SQL login credentials, may be unencrypted. |
     | Strict (TDS 8), trust off | SQL Server 2022+ with a trusted certificate. |

   - **Read-only** (default on), **AI Insights** (default on) and **Open (expose to agents)** (default on).
   - **Test connection** checks the unsaved values (30 s limit) and shows the result in the form; **Save** validates every field and shows the errors next to them; **Cancel** closes the form.
3. Right-click a saved connection and choose **Test Connection** to check it again later.

The text `${env:` is not accepted in any field, because the server would expand it from its own environment.

## Using it from Cursor, Claude Desktop and Claude Code

### Cursor (automatic)

In Cursor the extension registers the APoint-ms-sql server for you, through Cursor's own MCP extension API (`cursor.mcp.registerServer`); Cursor does not show servers offered through VS Code's `vscode.lm` MCP provider. The registration is named `APoint-ms-sql` (Cursor's MCP list shows it as `extension-APoint-ms-sql`: Cursor adds the `extension-` prefix itself), is created when at least one connection is open and usable, and follows your connections and the `msSqlMcp.insights`, `msSqlMcp.allowAdhocConnections` and `msSqlMcp.serverPath` settings. It is removed when no usable connection is left or the extension is disabled. No `~/.cursor/mcp.json` entry is needed.

If `~/.cursor/mcp.json` already has an `APoint-ms-sql` entry, or the legacy `ms-sql` entry of earlier versions (from **Register with Cursor / Claude...**), the extension warns on every start while that entry exists, because it duplicates the automatic registration. Remove that entry to avoid two APoint-ms-sql servers; the extension never edits the file on its own. **Don't show again** stops the warning; once the entry is gone that choice is forgotten, so a new duplicate warns again. Choose **Cursor** in the command below only for the `cursor-agent` CLI.

Agents never get `run_script`. That tool exists only for the extension's own query-window runner process (`MSSQL_SCRIPT_RUNNER=true`, set by the extension for that process only); it is never set for the agent server or for servers registered with external clients, and the 23 agent tools do not change.

### Cursor CLI, Claude Desktop and Claude Code

Run **Register with Cursor / Claude...** (the view's title bar, or the Command Palette) and pick the clients. The extension:

- copies the server exe to a per-user folder, so the registration survives extension upgrades;
- writes your open connections to a per-user `connections.json` in the extension's storage folder;
- merges an `APoint-ms-sql` entry into `~/.cursor/mcp.json` and `%APPDATA%\Claude\claude_desktop_config.json` (and the Microsoft Store copy of Claude Desktop, when present). Each file gets a timestamped `.bak` first, and a file with invalid JSON is never overwritten. The legacy `ms-sql` key of earlier versions is removed in the same write (the `.bak` keeps it), and for Claude Code `claude mcp remove --scope user ms-sql` runs first (a "not found" result is ignored);
- registers Claude Code with `claude mcp add-json --scope user`. When `claude` is not on `PATH`, it offers a Windows PowerShell command to copy instead.

Restart the client afterwards.

### SQL logins: `MSSQLMCP_PWD_<NAME>` placeholders

When a connection uses a SQL login, you choose how `connections.json` stores its password:

- **Write passwords to a per-user file**: the password is written in clear text to the file (per-user folder). Only after you confirm.
- **Use `${env:}` placeholders** (recommended): the file contains `Password="${env:MSSQLMCP_PWD_<NAME>}"`, and you define that user environment variable yourself. `<NAME>` is the connection name in upper case with every character other than `A-Z`, `0-9` and `_` replaced by `_`, e.g. `Prod-1` becomes `MSSQLMCP_PWD_PROD_1`. The quotes let the password contain `;`, `=` or `"`.

The extension keeps `connections.json` current when your connections change: closing, removing or making a connection read-only always takes effect in the file (restart the client to pick it up). A new connection that needs a new `MSSQLMCP_PWD_<NAME>` variable is left out of the file until you define the variable and choose **Re-register** (the warning names it), so clients never start with an unset variable. When no open connection is left, the file is deleted and registered clients cannot start the server until you open a connection and re-register.

## Read-only and Close

- **Read-only** connections refuse every write tool (`execute_sql`, `insert_data`, `update_data`, `create_table`, `drop_table`, `upsert_insight`, `install_insights_layer`, `refresh_insights`, `rebuild_baseline_insights`) and connect with `ApplicationIntent=ReadOnly`. They make no writes at all, including to the AI Insights tables. This is enforced by the server, not by SQL Server: also use a least-privilege login for anything that must never be written.
- **Close** removes a connection from what agents and the explorer can use, without deleting it. Its saved password is kept; **Open** brings it back. **Remove** deletes the connection and its saved password.

## How the explorer runs

The object explorer never uses the agent's server process. It starts its own private server process in which **every connection is forced read-only**, the AI Insights layer is off and ad-hoc connections are disabled, so browsing, DDL and the data view cannot modify anything. Query windows use a third private server process, the runner. It serves only open connections with **their own read-only setting** (not forced read-only) and is the only process with the extension-only `run_script` tool; agents never see that tool. Connection settings inherited from your environment (`CONNECTION_STRING`, `MSSQL_CONNECTIONS_FILE`) are ignored by all of these processes.

## Query window

- **New Query** (the new-file icon or the right-click menu of an open connection, or the command palette) opens an empty SQL editor bound to that connection.
- **Tab title**: every query window and object script shows where it runs, exactly as `<objectName> - <server> - <database>`: `Query 1 - DC∖DEV - Sales` for a query window, `dbo.vOrders - DC∖DEV - Sales` for an object script (`<name>` alone when the object has no schema). Query numbers count up from 1 in each editor session and skip numbers already open. For a raw connection string the title uses its `Data Source` / `Initial Catalog`, or `connection string` / `default` when they cannot be read. A `/` in a name is shown as `∕` (U+2215) and control characters are left out; a backslash is shown as `∖` (U+2216, a look-alike that is not a path separator): Cursor builds the tab label from what follows the last `/` or backslash, so `dc\dev16` would show as `dev16`. Only the tab title uses `∖`; the status bar, tooltips and dialogs show the real backslash. These documents always open in SQL mode (as a side effect, files on disk inside a folder named `~sql` are also highlighted as SQL). The title follows the connection: after **Change Connection** on a query window, or an edit of its connection's server or database, the document is saved, its tab is closed and the same document reopens under the new title, in the same editor group and still bound (its undo history does not carry over). A window with unsaved edits is never closed for this: the status bar shows the new connection at once, an information message says "The tab title updates after you save.", and the rename happens after the next save. A window with a query still running is renamed when the run ends, and its Results and Messages move to the renamed tab. Object scripts are retitled the same way when their connection's server or database is edited; after Change Connection they keep the title of where they were scripted from (Run then asks before applying them elsewhere).
- **Query windows are not files**: they are `mssql-sql:` documents kept in the extension's storage folder. **Ctrl+S** saves the text there. Closing a query window never loses saved text: the text is kept, and **Open Recent Query…** (command palette, or the history icon in the APoint-ms-sql view title) reopens it, newest first, for 30 days: query windows you keep open are not cleaned up; if a window stays closed everywhere for 30 days, its text is removed. Only an empty query window is deleted when you close it. Use **File > Save As...** for a real `.sql` file (the copy is a normal file and is not bound to a connection until you use Change Connection).
- **Open Recent Query…** reopens the same query document, binds it to its connection again and titles it by the rules above (its own `Query N` unless an open tab has that number, and the connection's current server and database). Query windows closed more than 30 days ago, and empty ones, are cleaned up about 15 seconds after the editor starts. A kept query window whose text exists but whose entry was lost is listed again as "Recovered query <id>".
- Two editor windows can open the same kept query window. They edit the same text: when both save, VS Code's usual save-conflict check (compare / overwrite) applies.
- The status bar shows the connection of the active SQL editor (a lock marks a read-only connection). Click it, or run **Change Connection**, to bind the editor to another open connection.
- On a read-only connection a query window runs only read-only single-SELECT batches, inside a transaction that is always rolled back. Anything else is refused with an error message.
- Every run opens a new session, outside the connection pool: `SET` options (including `SET TRANSACTION ISOLATION LEVEL`), `#temp` tables, `EXECUTE AS` and application roles do not carry over to the next run, and a transaction the script leaves open is rolled back at the end (with a warning). Use `COMMIT` inside the same run.
- The row cap per result set is the `msSqlMcp.query.maxRows` setting (default 1000, max 10000); further rows are counted but not kept.
- The server also caps each run (the script still runs to the end, and errors are always shown; each cap adds one warning): 50000 rows and about 32 MB of cell data in all, 200 result sets, 10000 messages (later info and row-count messages are dropped), and `GO n` up to 10000 (a larger n is refused for that batch). Text values longer than 65536 characters and binary values longer than 32768 bytes are cut and end with "… (truncated, N chars)" or "… (truncated, N bytes)"; the grid underlines such cells and their tooltip gives the full size.
- Values are shown exactly as SQL Server holds them: `decimal`, `numeric`, `money` and large `bigint` values arrive as exact text (not rounded to a JavaScript number) and are right-aligned like other numbers; binary values show SSMS-style as `0x…` hex.
- Bindings are kept per workspace; a query window or object script loses its binding when its tab is closed (one closed with unsaved edits keeps it until the next start).
- **Run** with F5 or the play button in the editor title runs the selection, or the whole document when nothing is selected. Results appear in the **APoint-ms-sql Results** panel at the bottom: a **Results** tab with one grid per result set and a **Messages** tab (errors in red; click a message with a line to jump to it). **Cancel** (the stop button in the editor title or in the panel) stops the run.
- The result grids have the data grid's resize, auto-fit and copy button. A header click sorts that result set locally, over the rows already loaded (ascending, descending, then the original order; stable, NULLs first when ascending, numbers by value); the caption then says "sorted locally (loaded rows only)".
- **F5 runs SQL** in an editor bound to a connection, instead of starting the debugger. While a debug session is running, F5 keeps its debugger meaning (Continue).
- With split editors, each editor group's title shows Run / Cancel for its own document, and the buttons act on that document even when another group is active.
- **Cancel, and closing the tab of a running query, stop the run on the server**: the running statement is cancelled and a transaction the script left open is rolled back. They do not undo work already done: statements that completed outside a transaction, and transactions the script already committed (for example earlier batches of a multi-batch script), stay applied.
- A running query is never killed by a connection change: the runner process is restarted for new runs, and the old one ends when its last run finishes.
- DDL views (`mssql-ddl:` documents) are never bound to a connection.

## Editing views, procedures and functions

Show DDL on a view, stored procedure, table-valued function or scalar function opens its script as an editable `mssql-sql:` document bound to that connection (the status bar shows it), instead of a read-only document. Its tab is titled `<schema>.<name> - <server> - <database>` (see Query window).

- The script is `CREATE OR ALTER` on SQL Server 2016 SP1 and later, and `ALTER` on older servers, so running it applies your change to the existing object. It is fetched through the explorer's read-only process; nothing is changed until you run it.
- **Read/write connection**: the editor title shows **Run** and F5 applies the script. The document is saved first. When at least one batch ran without an error, a success message says "Applied to '<connection>'." and the tree refreshes.
- **Read-only connection**: the editor title shows a disabled Run button whose tooltip explains that object changes can only be applied on a read/write connection. Edit the script if you like, then bind it to a read/write connection with **Change Connection** to apply it.
- **Wrong-target guard**: the document remembers the server and database it was scripted from (for a raw connection string, its `Data Source` / `Initial Catalog` when they can be read). If the connection now points at another server or database (for example after you edited the connection), or you bound the document to another connection with **Change Connection**, Run first asks: "This script was generated from <server>/<db> but will run on <server2>/<db2> (connection '<name>'). Run anyway?". Only **Run** applies it. Change Connection on such a document also tells you that the next run will ask. Open the object again from the tree to script it from its current connection.
- **No definition to edit**: CLR modules and modules created `WITH ENCRYPTION` have no T-SQL definition. They open as the read-only document with the server's warning instead of an editable document, so there is nothing to apply.
- **Indexed views**: `ALTER` on an indexed view drops its indexes. The script carries a warning (shown each time the document is opened from the server) and includes the index statements that recreate them; run the whole script.
- Warnings from the server are shown in an information message when the file opens. A long message is truncated; the full warning text is in the **APoint-ms-sql** Output channel when `msSqlMcp.logLevel` is `info` or more verbose (it never contains data rows).
- The documents live in the extension's storage folder (`sqldocs/object/<id>.sql`, where `<id>` is a hash of the exact connection, type, schema and name), one per object, so reopening an object reuses its document. Next to each one, `sqldocs/object/<id>.base.sql` keeps the last script loaded from the server, or the text last applied by a Run of the whole document that had no error (a Run of a selection, or one with an error, does not count). Object documents are never cleaned up automatically (they are small, one per object).
- **Opening an object again from the tree**: if the document has unsaved edits, Show DDL only reveals it ("Unsaved edits kept - close the editor to reload from the server."). If it holds saved edits that were never applied (its text differs from the base copy), a modal dialog asks "You have saved, unapplied edits to <obj>. Replace them with the current server version?": **Replace** reloads the server script, **Keep my edits** (or Escape) only reveals the document, and Run then asks before applying them when their origin is no longer known. It also asks once for a document saved by an earlier build, which has no base copy yet. Otherwise the document is reloaded with the current script from the server. The extension never edits the text in the editor itself, so Cursor shows no **Keep / Undo** review buttons for it. If scripting fails, you get the read-only document with the error and a Refresh button.
- **Upgrading from an earlier build**: object scripts used to be `.sql` files under the storage folder's `edits/` folder. About 15 seconds after the first start their bindings are removed and the folder is renamed to `edits.old-<yyyyMMddHHmmss>` (never deleted; the new path is written to the APoint-ms-sql Output at info level), so saved edits stay on disk; open the object again from the tree. Copy any unsaved work out of an old `edits/` tab before you update.
- Tables, indexes, foreign keys, triggers, types and security objects stay read-only.

## DDL history

Turn on **DDL history (audit trigger)** in Add / Edit Connection (off by default, available for every authentication type) to see how an object's script changed over time, as a side-by-side diff.

- **What it uses**: the `dbo.DDL_AuditLog` table and the `DDL_Audit` database trigger, which records every schema change (CREATE / ALTER / DROP) in that database with its time, login, host, program and full command text. A database that already has them (for example from your existing set-up) is used as is.
- **When something is missing, you are asked first**: after you save a connection with the option newly turned on, the extension checks the database. If the table or the trigger is missing on a read/write connection, a modal dialog asks "Create DDL history on <server>/<db> (connection '<name>')?" and lists exactly what will be created. `<server>/<db>` are the names the server itself reports (`@@SERVERNAME`, `DB_NAME()`), not the connection's fields (those are used only when the server does not report them). The dialog also says: "The trigger runs as the low-privilege user DDL_Audit_Writer (INSERT/SELECT on dbo.DDL_AuditLog only). If logging fails, the DDL statement still runs and is not logged, except when another trigger on DDL_AuditLog rolls back. To remove: DROP TRIGGER [DDL_Audit] ON DATABASE; DROP USER [DDL_Audit_Writer]." When the table already exists, it adds: "This grants INSERT and SELECT on the existing dbo.DDL_AuditLog to the new user DDL_Audit_Writer." Notes the server reports about an existing table (such as a `varchar(max)` `CommandText` storing non-Latin text lossy) are added to the dialog, or shown as an information message when no dialog is needed. Only **Create** creates them; nothing is created otherwise ("you can enable it later from Edit Connection"). Saving an edit that points a connection with the option on at another server or database runs the check again for the new database.
- **Existing objects are never changed**: an existing table or trigger is never altered, dropped or enabled. If `DDL_Audit` exists but is disabled, you get a warning that changes are not recorded, and it stays disabled. If an existing `dbo.DDL_AuditLog` does not have the columns the trigger writes, nothing is offered or created: a warning asks a DBA to align or rename the existing table.
- **Read-only connections** never create anything: if the table or trigger is missing, a warning asks you to have a DBA set it up, or to set it up from a read-write connection to the same database. The history itself can be read from a read-only connection.
- **Closed connections**: the check runs only on an open connection ("Open the connection to set up DDL history.").
- **Show DDL History** (history icon): in the editor title of an object's SQL document (the editable script of a view, procedure or function, or the read-only DDL of a table, trigger or type), and in the right-click menu of tables, views, procedures, functions, triggers and types in the tree, for connections with the option on. It lists the recorded changes newest first (date, event, login, host, program and size). Pick one to open a diff of that version against the version recorded before it (an empty side when it is the oldest). For views, procedures, functions and triggers, the first entry compares the latest recorded version with the definition currently in the database.
- If the table is missing when you open the history, or no change is listed and the `DDL_Audit` trigger is missing, the warning offers **Set up…**, which opens Edit Connection; saving it with the box checked runs the check again. A disabled trigger gets the "changes are not recorded" warning.
- The list shows only changes of that object type (for example a procedure's history leaves out a table with the same name; changes without a recorded type are kept), and a database trigger's list only changes without a schema. **Compare with current** uses the newest change that is not a DROP.
- **Only changes made after installation are recorded**: an object that has not changed since the trigger was created has no history yet.
- The diff documents are read-only. Command texts and logins are never written to the extension's log (trace logging records only counts and lengths).
- **How the trigger behaves**: it runs inside every DDL statement of every user of that database, as the loginless database user `DDL_Audit_Writer`, which the set-up creates with only INSERT and SELECT on `dbo.DDL_AuditLog` (never as `dbo`), so users need no permission on the table, and a database whose owner login was dropped keeps working. It records the real login (`ORIGINAL_LOGIN()`), sets the SET options it needs itself (old ODBC and MS Access sessions keep working), and if writing the log row fails (for example a too-narrow column), the DDL still goes through, unlogged, with the message "DDL_Audit: this change was not logged (error N).". While `dbo.DDL_AuditLog` has a trigger of its own, nothing is logged ("DDL_Audit: this change was not logged: DDL_AuditLog has triggers."; the set-up check and an empty history list then warn "dbo.DDL_AuditLog has triggers, so DDL_Audit does not record changes."), so such a trigger can neither roll the DDL back nor run with the writer's rights. What SQL Server does not allow it to rescue: an error that dooms the transaction by itself (for example a severity 20+ error, or another trigger on `dbo.DDL_AuditLog` that rolls back). It also prints a short "last modified" message (other logins that changed the object in the last month) for each DDL statement, in the Messages tab of a query window.
- **Permission to create it**: the set-up creates the user `DDL_Audit_Writer` (`WITHOUT LOGIN`), grants it INSERT and SELECT on `dbo.DDL_AuditLog`, checks that it can run as that user, and creates the trigger `WITH EXECUTE AS 'DDL_Audit_Writer'`; this needs `db_owner`. If any step fails, the user and table it just created are removed again and the warning gives the reason. A principal named `DDL_Audit_Writer` that is not a loginless user, or a loginless one with more rights than INSERT/SELECT on `dbo.DDL_AuditLog` (a role, an owned schema or another grant), is never reused or changed: the check before the dialog finds it, no dialog is shown, and the warning gives the server's reason.
- **Removing it**: `DROP TRIGGER [DDL_Audit] ON DATABASE; DROP USER [DDL_Audit_Writer];` stops all logging at once. If you also want the history gone, back up `dbo.DDL_AuditLog` first, then `DROP TABLE dbo.DDL_AuditLog;`. A table created by the extension also has the index `IX_DDL_AuditLog_ObjectName`, which is dropped with it.
- An existing `dbo.DDL_AuditLog` is used only when its columns are wide enough and of the right types (for example `ObjectName` at least 100 characters, `CommandXML` of type `xml`); otherwise nothing is created and a warning asks a DBA to align the table (the server's `ddl_history install` error lists each problem). A `varchar(max)` `CommandText` is accepted, but non-Latin text in DDL is then stored lossy.

## Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `msSqlMcp.insights` | `true` | AI Insights layer for the agent server (the explorer never uses it). |
| `msSqlMcp.allowAdhocConnections` | `false` | Lets agents open ad-hoc connections from a raw connection string. The server still refuses Windows / Entra identity, writable and file-attach connections unless the operator enables them (see the server README). |
| `msSqlMcp.dataViewRows` | `200` | Data View's initial TOP (1-10000); the panel's TOP box changes it per view. |
| `msSqlMcp.export.neutralizeFormulas` | `true` | Export CSV prefixes text values that start with `=` `+` `-` `@` (or a tab / CR) with `'` so spreadsheets do not run them as formulas. |
| `msSqlMcp.query.maxRows` | `1000` | Rows kept per result set when a query window runs (1-10000); further rows are only counted. |
| `msSqlMcp.serverPath` | bundled | Path to a different `MssqlMcp.exe`. |
| `msSqlMcp.logLevel` | `error` (installed) | Output channel verbosity: `off`, `error`, `warn`, `info`, `debug`, `trace`. |

## Privacy

- **Principal names are visible to agents.** Logins, users and roles are listed and scripted by the explorer and by the agent tools. They are often personal names (for example Active Directory accounts). Passwords, hashes and SIDs are never returned.
- **The data view and query results stay on your machine.** Rows are shown in the editor and in the Results panel and are never sent anywhere automatically. They leave the grid only when you copy them (a cell, a row, a selection, or Ctrl+A) or export them to a CSV file, which asks for confirmation every time. The Output channel logs only row counts, never values.
- **Cell viewer tabs are normal editor tabs**; AI assistants in your editor may read open tabs as context. Close viewer tabs holding client personal data before using AI chat.
- **Agents see what they query.** Data returned by `read_data` goes to the AI model the agent uses. Use read-only connections and least-privilege logins for databases with personal data.
- **Trace logging.** At `msSqlMcp.logLevel` = `trace`, the Output channel records tool arguments and results, which may include SQL text and object definitions (for `read_data` and `run_script` only row and message counts are logged). Use `trace` only for troubleshooting and clear the channel afterwards.
- Passwords are never written to settings, logs, the Output channel, tree labels or DDL documents.

## License

MIT. See `LICENSE`.
