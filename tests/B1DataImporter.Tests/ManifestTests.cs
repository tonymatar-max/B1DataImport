using B1DataImporter.Api.Services.Connectors;
using B1DataImporter.Api.Services.Connectors.Rest;
using Xunit;

namespace B1DataImporter.Tests;

public class TargetTypeTests
{
    [Theory]
    [InlineData("string", "Edm.String")]
    [InlineData("int", "Edm.Int32")]
    [InlineData("integer", "Edm.Int32")]
    [InlineData("decimal", "Edm.Decimal")]
    [InlineData("bool", "Edm.Boolean")]
    [InlineData("datetime", "Edm.DateTimeOffset")]
    [InlineData("weird-unknown", "Edm.String")]
    public void Parse_then_ToEdm_maps_to_expected_edm_tag(string raw, string expectedEdm)
        => Assert.Equal(expectedEdm, TargetTypes.ToEdm(TargetTypes.Parse(raw)));
}

public class ConnectorManifestTests
{
    [Fact]
    public void ToEntities_projects_fields_keys_and_edm_types()
    {
        var manifest = new ConnectorManifest
        {
            Id = "demo",
            Entities =
            {
                new ManifestEntity
                {
                    Name = "Products",
                    KeyField = "ID",
                    Fields =
                    {
                        new ManifestField { Name = "ID", Type = "int" },       // key via KeyField
                        new ManifestField { Name = "Name", Type = "string", Nullable = false, MaxLength = 100 },
                        new ManifestField { Name = "Status", Type = "enum", EnumMembers = new() { "Active", "Closed" } },
                    },
                },
            },
        };

        var entity = Assert.Single(manifest.ToEntities());
        Assert.Equal("Products", entity.Name);

        var id = entity.Properties.Single(p => p.Name == "ID");
        Assert.True(id.IsKey);                       // inferred from KeyField
        Assert.Equal("Edm.Int32", id.Type);

        var name = entity.Properties.Single(p => p.Name == "Name");
        Assert.False(name.Nullable);
        Assert.Equal(100, name.MaxLength);

        var status = entity.Properties.Single(p => p.Name == "Status");
        Assert.Equal("Edm.String", status.Type);
        Assert.Equal(new[] { "Active", "Closed" }, status.EnumMembers);
    }
}

public class ManifestStoreTests
{
    [Fact]
    public void Reload_loads_manifests_from_content_root_connectors_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "b1imp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "connectors"));
        File.WriteAllText(Path.Combine(root, "connectors", "demo.json"),
            """
            {
              "id": "demo", "displayName": "Demo", "auth": { "type": "none" },
              "entities": [
                { "name": "Things", "keyField": "ID",
                  "fields": [ { "name": "ID", "type": "int", "isKey": true } ] }
              ]
            }
            """);

        var store = new ManifestStore(new FakeEnv(root),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ManifestStore>.Instance);

        var m = store.Get("demo");
        Assert.Equal("Demo", m.DisplayName);
        Assert.Equal("Things", Assert.Single(m.Entities).Name);
        Assert.Throws<InvalidOperationException>(() => store.Get("missing"));

        Directory.Delete(root, recursive: true);
    }
}
