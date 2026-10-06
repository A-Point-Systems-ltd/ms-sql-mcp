using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Mssql.McpServer.Connections;
using Mssql.McpServer.Connections.Managed;

namespace MssqlMcp.Tests.Connections.Managed;

/// <summary>Reversible fake: tests must not depend on the machine's DPAPI keys.</summary>
internal sealed class FakeProtector : ISecretProtector
{
    public bool FailUnprotect { get; set; }

    public string Protect(string secret) => "enc:" + secret;

    public string? TryUnprotect(string blob) => FailUnprotect || !blob.StartsWith("enc:", StringComparison.Ordinal) ? null : blob[4..];
}

public sealed class ManagedConnectionServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mssqlmcp-managed-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProtector _protector = new();

    private string FilePath => Path.Combine(_dir, "connections.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private (ManagedConnectionService Service, ConnectionRegistry Registry, ManagedConnectionStore Store) Create(params ConnectionProfile[] configured)
    {
        var registry = new ConnectionRegistry(configured);
        var store = new ManagedConnectionStore(FilePath);
        var service = new ManagedConnectionService(store, registry, _protector, NullLogger<ManagedConnectionService>.Instance);
        service.Sync();
        return (service, registry, store);
    }

    private static ManagedConnectionInput Win(string name = "dev") =>
        new() { Name = name, Auth = ManagedAuth.Windows, Server = "srv", Database = "db" };

    private static ManagedConnectionInput Sql(string name = "prod", string password = "s3cret") =>
        new() { Name = name, Auth = ManagedAuth.Sql, Server = "srv", Database = "db", User = "sa", Password = password };

    [Fact]
    public void Missing_file_means_no_managed_connections()
    {
        var (service, registry, _) = Create();
        var list = service.List();
        Assert.Empty(list.Managed);
        Assert.Null(list.FileError);
        Assert.Equal(0, registry.Count);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Add_registers_immediately_and_persists()
    {
        var (service, registry, store) = Create();

        var result = service.Save(Win(), isNew: true);

        Assert.True(result.Success);
        var profile = Assert.IsType<ConnectionProfile>(registry.Find("dev"));
        Assert.Equal(ConnectionSource.Managed, profile.Source);
        Assert.True(profile.ReadOnly);
        Assert.True(registry.IsOpen("dev"));
        Assert.Equal("dev", Assert.Single(store.Load()).Name);
    }

    [Fact]
    public void Password_is_stored_only_encrypted_and_never_listed()
    {
        var (service, registry, _) = Create();
        Assert.True(service.Save(Sql(), isNew: true).Success);

        var json = File.ReadAllText(FilePath);
        Assert.DoesNotContain("\"s3cret\"", json, StringComparison.Ordinal);
        Assert.Contains("enc:s3cret", json, StringComparison.Ordinal);
        Assert.Equal("s3cret", new SqlConnectionStringBuilder(registry.Find("prod")!.ConnectionString).Password);

        var view = Assert.Single(service.List().Managed);
        Assert.True(view.HasPassword);
        Assert.DoesNotContain("s3cret", System.Text.Json.JsonSerializer.Serialize(view), StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_with_empty_password_keeps_the_saved_one()
    {
        var (service, registry, _) = Create();
        Assert.True(service.Save(Sql(), isNew: true).Success);

        Assert.True(service.Save(Sql(password: "") with { ReadOnly = false }, isNew: false).Success);

        var profile = registry.Find("prod")!;
        Assert.False(profile.ReadOnly);
        Assert.Equal("s3cret", new SqlConnectionStringBuilder(profile.ConnectionString).Password);
    }

    [Fact]
    public void Edit_replaces_the_registered_profile_and_raises_Replaced()
    {
        var (service, registry, _) = Create();
        Assert.True(service.Save(Win(), isNew: true).Success);
        var events = new List<ConnectionChangedEventArgs>();
        registry.Changed += (_, e) => events.Add(e);

        Assert.True(service.Save(Win() with { Database = "other" }, isNew: false).Success);

        Assert.Equal("other", new SqlConnectionStringBuilder(registry.Find("dev")!.ConnectionString).InitialCatalog);
        Assert.Equal(ConnectionChangeKind.Replaced, Assert.Single(events).Kind);
    }

    [Fact]
    public void Add_refuses_a_name_used_by_any_connection_case_insensitively()
    {
        var configured = new ConnectionProfile("Shared", "Server=x;Database=y;Integrated Security=True", false, true, ConnectionSource.Configured);
        var (service, _, _) = Create(configured);
        Assert.True(service.Save(Win(), isNew: true).Success);

        Assert.Equal("A connection with this name already exists.", service.Save(Win("DEV"), isNew: true).Errors["name"]);
        Assert.Equal("A connection with this name already exists.", service.Save(Win("shared"), isNew: true).Errors["name"]);
    }

    [Fact]
    public void Validation_errors_do_not_write_the_file()
    {
        var (service, _, _) = Create();
        var result = service.Save(Win() with { Server = "" }, isNew: true);
        Assert.False(result.Success);
        Assert.True(result.Errors.ContainsKey("server"));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Remove_unregisters_and_refuses_non_managed_names()
    {
        var configured = new ConnectionProfile("cfg", "Server=x;Database=y;Integrated Security=True", false, true, ConnectionSource.Configured);
        var (service, registry, store) = Create(configured);
        Assert.True(service.Save(Win(), isNew: true).Success);

        Assert.True(service.Remove("DEV").Success);
        Assert.Null(registry.Find("dev"));
        Assert.Empty(store.Load());

        Assert.False(service.Remove("cfg").Success);
        Assert.NotNull(registry.Find("cfg"));
    }

    [Fact]
    public void Changes_by_another_process_are_picked_up()
    {
        var (service, registry, _) = Create();
        var otherStore = new ManagedConnectionStore(FilePath);
        var other = new ManagedConnectionService(otherStore, new ConnectionRegistry([]), _protector, NullLogger<ManagedConnectionService>.Instance);

        Assert.True(other.Save(Win("fromOther"), isNew: true).Success);
        File.SetLastWriteTimeUtc(FilePath, DateTime.UtcNow.AddSeconds(5)); // the stat check must see a change even on coarse clocks
        service.EnsureCurrent();

        Assert.NotNull(registry.Find("fromOther"));
    }

    [Fact]
    public void Undecryptable_password_is_reported_and_not_registered()
    {
        var (service, registry, _) = Create();
        Assert.True(service.Save(Sql(), isNew: true).Success);

        _protector.FailUnprotect = true;
        service.Sync();

        Assert.Null(registry.Find("prod"));
        Assert.Contains("cannot be decrypted", Assert.Single(service.List().Managed).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Corrupt_file_is_reported_left_untouched_and_keeps_registered_connections()
    {
        var (service, registry, _) = Create();
        Assert.True(service.Save(Win(), isNew: true).Success);
        File.WriteAllText(FilePath, "{ not json");
        File.SetLastWriteTimeUtc(FilePath, DateTime.UtcNow.AddSeconds(5));

        var list = service.List();
        Assert.NotNull(list.FileError);
        Assert.NotNull(registry.Find("dev"));
        Assert.Throws<ManagedConnectionFileException>(() => service.Save(Win("x"), isNew: true));
        Assert.Equal("{ not json", File.ReadAllText(FilePath));
    }

    [Fact]
    public void Name_colliding_with_a_configured_connection_is_reported_not_registered()
    {
        File.WriteAllText(FilePath.EnsureDir(), """{"version":1,"connections":[{"name":"cfg","auth":"windows","server":"s","database":"d"}]}""");
        var configured = new ConnectionProfile("cfg", "Server=x;Database=y;Integrated Security=True", false, true, ConnectionSource.Configured);
        var (service, registry, _) = Create(configured);

        Assert.Equal(ConnectionSource.Configured, registry.Find("cfg")!.Source);
        Assert.Contains("already used", Assert.Single(service.List().Managed).Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Others_are_listed_read_only()
    {
        var configured = new ConnectionProfile("cfg", "Server=x;Database=y;Integrated Security=True", true, true, ConnectionSource.Configured);
        var (service, _, _) = Create(configured);
        var other = Assert.Single(service.List().Others);
        Assert.Equal("cfg", other.Name);
        Assert.Equal("Configured", other.Source);
        Assert.Equal("x", other.DataSource);
    }

    [Fact]
    public void Save_keeps_a_backup_of_the_previous_file()
    {
        var (service, _, _) = Create();
        Assert.True(service.Save(Win("a"), isNew: true).Success);
        Assert.True(service.Save(Win("b"), isNew: true).Success);
        Assert.Contains("\"a\"", File.ReadAllText(FilePath + ".bak"), StringComparison.Ordinal);
        Assert.DoesNotContain("\"b\"", File.ReadAllText(FilePath + ".bak"), StringComparison.Ordinal);
    }
}

internal static class PathTestExtensions
{
    public static string EnsureDir(this string path)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
