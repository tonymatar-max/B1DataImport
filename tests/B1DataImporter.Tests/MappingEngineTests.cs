using B1DataImporter.Api.Domain;
using B1DataImporter.Api.Services.Connectors;
using B1DataImporter.Api.Services.Connectors.Rest;
using B1DataImporter.Api.Services.Mapping;
using Xunit;

namespace B1DataImporter.Tests;

/// <summary>
/// Exercises the mapping engine against connector metadata that comes from a manifest (not B1) and
/// a fake lookup — proving the pipeline (transforms, coercion, name→code lookup) is target-agnostic.
/// </summary>
public class MappingEngineTests
{
    private static ManifestEntity ProductEntity() => new()
    {
        Name = "Products",
        KeyField = "ID",
        Fields =
        {
            new ManifestField { Name = "ID", Type = "int" },
            new ManifestField { Name = "Name", Type = "string", Nullable = false, MaxLength = 100 },
            new ManifestField { Name = "Price", Type = "decimal" },
            new ManifestField { Name = "CategoryID", Type = "int" },
        },
    };

    [Fact]
    public async Task Builds_payload_with_direct_expression_and_lookup_transforms()
    {
        var entity = ProductEntity().ToTargetEntity();
        var spec = new MappingSpec
        {
            Header =
            {
                new FieldSpec { TargetField = "Name",  SourceColumn = "product_name",
                                Transform = TransformKind.Expression, Expression = "{product_name|trim|upper}" },
                new FieldSpec { TargetField = "Price", SourceColumn = "unit_price" },
                new FieldSpec { TargetField = "CategoryID", SourceColumn = "category",
                                Transform = TransformKind.B1Lookup,
                                B1Lookup = new B1LookupSpec { Entity = "Categories", MatchField = "Name",
                                                             ReturnField = "ID", FailIfMissing = true } },
            },
        };
        var row = new Dictionary<string, object?>
        {
            ["product_name"] = "  widget  ",
            ["unit_price"] = "12.50",
            ["category"] = "Hardware",
        };
        var lookups = new FakeLookup(new() { ["Hardware"] = "7" });

        var outcome = await new RecordBuilder().BuildAsync(
            spec, new() { row }, entity, lookups, CancellationToken.None);

        Assert.True(outcome.Ok, string.Join("; ", outcome.Errors));
        Assert.Equal("WIDGET", outcome.Payload!["Name"]);
        Assert.Equal(12.5d, Convert.ToDouble(outcome.Payload["Price"]));         // coerced to a numeric
        Assert.Equal(7L, Convert.ToInt64(outcome.Payload["CategoryID"]));        // lookup result coerced to integer
    }

    [Fact]
    public async Task Failing_required_lookup_reports_error_not_throw()
    {
        var entity = ProductEntity().ToTargetEntity();
        var spec = new MappingSpec
        {
            Header =
            {
                new FieldSpec { TargetField = "CategoryID", SourceColumn = "category",
                                Transform = TransformKind.B1Lookup,
                                B1Lookup = new B1LookupSpec { Entity = "Categories", MatchField = "Name",
                                                             ReturnField = "ID", FailIfMissing = true } },
            },
        };
        var row = new Dictionary<string, object?> { ["category"] = "DoesNotExist" };
        var lookups = new FakeLookup(new());   // empty → miss

        var outcome = await new RecordBuilder().BuildAsync(
            spec, new() { row }, entity, lookups, CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Contains(outcome.Errors, e => e.Contains("CategoryID"));
    }
}

public class TargetConnectorRegistryTests
{
    private sealed class StubConnector : ITargetConnector
    {
        public StubConnector(ConnectionKind kind) => Kind = kind;
        public ConnectionKind Kind { get; }
        public string DisplayName => Kind.ToString();
        public Task<ITargetSession> OpenAsync(ConnectionDef c, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public void Get_resolves_by_kind_and_throws_for_unregistered()
    {
        var registry = new TargetConnectorRegistry(new ITargetConnector[]
        {
            new StubConnector(ConnectionKind.SapB1),
            new StubConnector(ConnectionKind.Rest),
        });

        Assert.Equal(ConnectionKind.Rest, registry.Get(ConnectionKind.Rest).Kind);
        Assert.Throws<InvalidOperationException>(() => registry.Get(ConnectionKind.SqlServer));
    }
}
