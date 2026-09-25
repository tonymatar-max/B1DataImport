using B1DataImporter.Api.Domain;
using B1DataImporter.Api.Services.B1;   // ISecretProtector

namespace B1DataImporter.Api.Services.Connectors.Rest;

/// <summary>
/// The manifest-driven REST connector: the second <see cref="ITargetConnector"/> and the proof
/// that the platform is no longer B1-only. It handles every <see cref="ConnectionKind.Rest"/>
/// connection; which system a connection actually targets is decided by its
/// <see cref="ConnectionDef.ConnectorManifestId"/>, resolved from the <see cref="ManifestStore"/>.
/// Adding a new REST/OData system needs only a manifest JSON file — no new code.
/// </summary>
public class RestManifestConnector : ITargetConnector
{
    private readonly ManifestStore _manifests;
    private readonly ISecretProtector _secrets;

    public RestManifestConnector(ManifestStore manifests, ISecretProtector secrets)
    {
        _manifests = manifests;
        _secrets = secrets;
    }

    public ConnectionKind Kind => ConnectionKind.Rest;
    public string DisplayName => "REST / OData (manifest-driven)";

    public Task<ITargetSession> OpenAsync(ConnectionDef connection, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(connection.ConnectorManifestId))
            throw new InvalidOperationException(
                "This REST connection has no ConnectorManifestId set; cannot decide which system to target.");
        if (string.IsNullOrWhiteSpace(connection.BaseUrl))
            throw new InvalidOperationException("This REST connection has no BaseUrl.");

        var manifest = _manifests.Get(connection.ConnectorManifestId);
        var client = new RestApiClient(
            connection.BaseUrl,
            manifest.Auth,
            connection.UserName,
            _secrets.Unprotect(connection.SecretProtected),
            connection.IgnoreSslErrors);

        return Task.FromResult<ITargetSession>(new RestManifestSession(client, manifest));
    }
}
