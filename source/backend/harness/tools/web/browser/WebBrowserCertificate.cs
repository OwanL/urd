using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Urd.Core.Tools;

internal static class WebBrowserCertificate {
  public static X509Certificate2 Create() {
    using RSA key = RSA.Create(2048);
    var request = new CertificateRequest("CN=Urd ephemeral web transport", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    DateTimeOffset now = DateTimeOffset.UtcNow;
    using X509Certificate2 generated = request.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(1));
    // Schannel needs a Windows key container. Don't persist it after the certificate is disposed.
    byte[] pfx = generated.Export(X509ContentType.Pkcs12);
    try {
      return X509CertificateLoader.LoadPkcs12(pfx, null);
    } finally {
      CryptographicOperations.ZeroMemory(pfx);
    }
  }
}
