using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Exam.Infrastructure.Security;

public interface IPublicationCertificateProvider
{
    PublicationCertificateLease Acquire();
}

/// <summary>所有证书副本只含公钥；租约负责释放，不跨请求保留系统证书句柄。</summary>
public sealed class PublicationCertificateLease(IReadOnlyList<NodeKeyRecipient> recipients):IDisposable
{
    public IReadOnlyList<NodeKeyRecipient> Recipients {get;}=recipients;
    public void Dispose(){foreach(var recipient in Recipients)recipient.Certificate.Dispose();}
}

public sealed class CertificateStorePublicationProvider(IReadOnlyDictionary<string,ServerCertificateSource> certificates,
    ManagedNodeEncryptionPolicy policy):IPublicationCertificateProvider
{
    public CertificateStorePublicationProvider(IReadOnlyDictionary<string,string> thumbprints,
        ManagedNodeEncryptionPolicy policy)
        : this(thumbprints.ToDictionary(pair => pair.Key, pair => new ServerCertificateSource(pair.Value),
            StringComparer.Ordinal), policy) { }

    public PublicationCertificateLease Acquire()
    {
        if(policy.RequiredNodeIds.Count==0||!policy.RequiredNodeIds.SetEquals(certificates.Keys))
            throw new CryptographicException("受管节点与发布证书配置不一致。");
        var recipients=new List<NodeKeyRecipient>();
        try
        {
            foreach(var node in policy.RequiredNodeIds.Order(StringComparer.Ordinal))
            {
                using var original=certificates[node].Load(requirePrivateKey:false);
                var now=DateTime.UtcNow;
                if(now<original.NotBefore.ToUniversalTime()||now>original.NotAfter.ToUniversalTime())throw new CryptographicException("受管节点的公开证书尚未生效或已过期。");
                using(var rsa=original.GetRSAPublicKey())
                    if(rsa is null||rsa.KeySize<2048)throw new CryptographicException("发布公钥须为至少2048位的 RSA 证书。");
                // RawData 是公开 DER，发布租约从不携带源证书中的私钥。
                recipients.Add(new(node,X509CertificateLoader.LoadCertificate(original.RawData)));
            }
            return new(recipients);
        }
        catch{foreach(var recipient in recipients)recipient.Certificate.Dispose();throw;}
    }
}
