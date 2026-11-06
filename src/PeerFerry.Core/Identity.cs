using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PeerFerry.Core;

public sealed class Identity : IDisposable
{
    public X509Certificate2 Certificate { get; }
    public string Id => Fingerprint(Certificate);
    public Identity(string directory)
    {
        var path = Path.Combine(directory, "identity.pfx");
        if (File.Exists(path)) Certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        else
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=PeerFerry", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
            File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx));
            Certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
        }
    }
    public static string Fingerprint(X509Certificate certificate) => Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));
    public void Dispose() => Certificate.Dispose();
}
