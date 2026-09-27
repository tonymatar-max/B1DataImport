using B1DataImporter.Api.Domain;
using B1DataImporter.Api.Models;
using B1DataImporter.Api.Services;
using B1DataImporter.Api.Services.Connectors;
using B1DataImporter.Api.Services.Connectors.Rest;
using Xunit;

namespace B1DataImporter.Tests;

public class SourceConnectorRegistryTests
{
    private sealed class Stub : ISourceConnector
    {
        public Stub(ConnectionKind k) => Kind = k;
        public ConnectionKind Kind { get; }
        public string DisplayName => Kind.ToString();
        public SourceSchema Inspect(ConnectionDef c, string o, int p = 20) => throw new NotImplementedException();
        public IEnumerable<Dictionary<string, object?>> ReadAll(ConnectionDef c, string o, string? q)
            => throw new NotImplementedException();
    }

    [Fact]
    public void Resolves_by_kind_and_reports_membership()
    {
        var reg = new SourceConnectorRegistry(new ISourceConnector[] { new Stub(ConnectionKind.Rest) });
        Assert.True(reg.Has(ConnectionKind.Rest));
        Assert.False(reg.Has(ConnectionKind.SqlServer));
        Assert.Equal(ConnectionKind.Rest, reg.Get(ConnectionKind.Rest).Kind);
        Assert.Throws<InvalidOperationException>(() => reg.Get(ConnectionKind.SapB1));
    }
}

public class RestSourceConnectorTests
{
    private sealed class PlainSecrets : ISecretProtector
    {
        public string? Protect(string? plain) => plain;
        public string? Unprotect(string? cipher) => cipher;
    }

    private static ManifestStore Store()
    {
        var root = Path.Combine(Path.GetTempPath(), "b1imp-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "connectors"));
        File.WriteAllText(Path.Combine(root, "connectors", "demo.json"), """
        {
          "id": "demo", "displayName": "Demo", "auth": { "type": "none" },
          "entities": [ { "name": "Products", "keyField": "ID",
            "fields": [ { "name": "ID", "type": "int", "isKey": true }, { "name": "Name", "type": "string" } ] } ]
        }
        """);
        return new ManifestStore(new FakeEnv(root),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ManifestStore>.Instance);
    }

    [Fact]
    public void ReadAll_without_base_url_throws_before_any_network_call()
    {
        var connector = new RestSourceConnector(Store(), new PlainSecrets());
        var conn = new ConnectionDef { Kind = ConnectionKind.Rest, ConnectorManifestId = "demo" }; // no BaseUrl
        Assert.Throws<InvalidOperationException>(() => connector.ReadAll(conn, "Products", null).ToList());
    }

    [Fact]
    public void Inspect_without_manifest_id_throws()
    {
        var connector = new RestSourceConnector(Store(), new PlainSecrets());
        var conn = new ConnectionDef { Kind = ConnectionKind.Rest, BaseUrl = "https://x.test/" };
        Assert.Throws<InvalidOperationException>(() => connector.Inspect(conn, "Products"));
    }
}
