using B1DataImporter.Api.Domain;
using B1DataImporter.Api.Services.B1;

namespace B1DataImporter.Api.Services.Connectors.B1;

/// <summary>
/// The SAP Business One target connector: opens a Service Layer session for a connection.
/// This is the first <see cref="ITargetConnector"/> implementation; it wraps the existing
/// <see cref="ServiceLayerClient"/> / <see cref="MetadataService"/> without changing their behavior.
/// </summary>
public class B1TargetConnector : ITargetConnector
{
    private readonly MetadataService _metadata;
    private readonly ISecretProtector _secrets;

    public B1TargetConnector(MetadataService metadata, ISecretProtector secrets)
    {
        _metadata = metadata;
        _secrets = secrets;
    }

    public ConnectionKind Kind => ConnectionKind.SapB1;
    public string DisplayName => "SAP Business One (Service Layer)";

    public async Task<ITargetSession> OpenAsync(ConnectionDef connection, CancellationToken ct = default)
    {
        var client = new ServiceLayerClient(ToInfo(connection, _secrets));
        await client.LoginAsync(ct);
        return new B1TargetSession(client, _metadata, connection);
    }

    /// <summary>Map a stored connection definition to Service Layer login info, decrypting the secret.</summary>
    public static B1ConnectionInfo ToInfo(ConnectionDef c, ISecretProtector secrets) => new()
    {
        BaseUrl = c.BaseUrl ?? "",
        CompanyDB = c.CompanyDB ?? "",
        UserName = c.UserName ?? "",
        Password = secrets.Unprotect(c.SecretProtected) ?? "",
        IgnoreSslErrors = c.IgnoreSslErrors,
    };
}
