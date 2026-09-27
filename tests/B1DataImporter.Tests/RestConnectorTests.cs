using B1DataImporter.Api.Domain;
using B1DataImporter.Api.Services;
using B1DataImporter.Api.Services.Connectors.Rest;
using Xunit;

namespace B1DataImporter.Tests;

/// <summary>Unit tests for the REST connector's non-network behavior (manifest → session → metadata).</summary>
public class RestConnectorTests
{
    private sealed class PlainSecrets : ISecretProtector
    {
        public string? Protect(string? plain) => plain;
        public string? Unprotect(string? cipher) => cipher;
    }

    private static ManifestStore StoreWith(string json)
    {
        var root = Path.Combine(Path.GetTempPath(), "b1imp-rest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "connectors"));
        File.WriteAllText(Path.Combine(root, "connectors", "demo.json"), json);
        return new ManifestStore(new FakeEnv(root),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ManifestStore>.Instance);
    }

    private const string Manifest = """
    {
      "id": "demo", "displayName": "Demo", "auth": { "type": "bearer" },
      "entities": [
        { "name": "Products", "keyField": "ID",
          "fields": [
            { "name": "ID", "type": "int", "isKey": true },
            { "name": "Name", "type": "string", "nullable": false }
          ] }
      ]
    }
    """;

    [Fact]
    public async Task Open_then_GetEntities_returns_manifest_entities()
    {
        var connector = new RestManifestConnector(StoreWith(Manifest), new PlainSecrets());
        var conn = new ConnectionDef
        {
            Kind = ConnectionKind.Rest, BaseUrl = "https://example.test/odata/", ConnectorManifestId = "demo",
        };

        using var session = await connector.OpenAsync(conn);
        var entities = await session.GetEntitiesAsync();

        var e = Assert.Single(entities);
        Assert.Equal("Products", e.Name);
        Assert.True(e.Properties.Single(p => p.Name == "ID").IsKey);
    }

    [Fact]
    public async Task Open_without_manifest_id_throws()
    {
        var connector = new RestManifestConnector(StoreWith(Manifest), new PlainSecrets());
        var conn = new ConnectionDef { Kind = ConnectionKind.Rest, BaseUrl = "https://example.test/" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.OpenAsync(conn));
    }

    [Fact]
    public async Task Open_without_base_url_throws()
    {
        var connector = new RestManifestConnector(StoreWith(Manifest), new PlainSecrets());
        var conn = new ConnectionDef { Kind = ConnectionKind.Rest, ConnectorManifestId = "demo" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => connector.OpenAsync(conn));
    }

    [Fact]
    public async Task FormatKeyPredicate_quotes_strings_and_leaves_numbers_bare()
    {
        var connector = new RestManifestConnector(StoreWith(Manifest), new PlainSecrets());
        var conn = new ConnectionDef
        {
            Kind = ConnectionKind.Rest, BaseUrl = "https://example.test/", ConnectorManifestId = "demo",
        };
        using var session = await connector.OpenAsync(conn);
        var entity = (await session.GetEntitiesAsync()).Single();
        var idProp = entity.Properties.Single(p => p.Name == "ID");     // Edm.Int32
        var nameProp = entity.Properties.Single(p => p.Name == "Name"); // Edm.String

        Assert.Equal("42", session.FormatKeyPredicate(42, idProp));
        Assert.Equal("'A''B'", session.FormatKeyPredicate("A'B", nameProp));
    }
}
