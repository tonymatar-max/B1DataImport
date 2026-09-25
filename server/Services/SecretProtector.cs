using Microsoft.AspNetCore.DataProtection;

namespace B1DataImporter.Api.Services;

/// <summary>
/// Encrypts stored credentials (B1 password, SQL connection strings) at rest so the
/// SQLite file never contains plaintext secrets.
/// </summary>
public interface ISecretProtector
{
    string? Protect(string? plain);
    string? Unprotect(string? cipher);
}

public class SecretProtector : ISecretProtector
{
    private readonly IDataProtector _protector;

    public SecretProtector(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("B1DataImporter.Secrets.v1");

    public string? Protect(string? plain)
        => string.IsNullOrEmpty(plain) ? null : _protector.Protect(plain);

    public string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try { return _protector.Unprotect(cipher); }
        catch { return null; }   // key rotated or DB copied to another machine
    }
}
