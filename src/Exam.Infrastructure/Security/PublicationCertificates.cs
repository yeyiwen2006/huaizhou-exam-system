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

public sealed class CertificateStorePublicationProvider(IReadOnlyDictionary<string,string> thumbprints,
    ManagedNodeEncryptionPolicy policy):IPublicationCertificateProvider
{
    public PublicationCertificateLease Acquire()
    {
        if(!OperatingSystem.IsWindows())throw new CryptographicException("生产发布证书须从 Windows 受保护证书存储读取。");
        if(policy.RequiredNodeIds.Count==0||!policy.RequiredNodeIds.SetEquals(thumbprints.Keys))
            throw new CryptographicException("受管节点与发布证书配置不一致。");
        var recipients=new List<NodeKeyRecipient>();
        try
        {
            using var store=new X509Store(StoreName.My,StoreLocation.LocalMachine);store.Open(OpenFlags.ReadOnly);
            foreach(var node in policy.RequiredNodeIds.Order(StringComparer.Ordinal))
            {
                var thumb=thumbprints[node].Replace(" ","",StringComparison.Ordinal).ToUpperInvariant();
                if(string.IsNullOrWhiteSpace(thumb))throw new CryptographicException("受管节点尚未配置发布公钥证书。");
                var found=store.Certificates.Find(X509FindType.FindByThumbprint,thumb,validOnly:false);
                if(found.Count!=1)throw new CryptographicException("受管节点的公开证书未安装或不唯一。");
                var original=found[0];var now=DateTime.UtcNow;
                if(now<original.NotBefore.ToUniversalTime()||now>original.NotAfter.ToUniversalTime())throw new CryptographicException("受管节点的公开证书尚未生效或已过期。");
                using(var rsa=original.GetRSAPublicKey())
                    if(rsa is null||rsa.KeySize<2048)throw new CryptographicException("发布公钥须为至少2048位的 RSA 证书。");
                // RawData 是公开 DER，加载副本不携带证书存储中的私钥关联。
                recipients.Add(new(node,X509CertificateLoader.LoadCertificate(original.RawData)));
            }
            return new(recipients);
        }
        catch{foreach(var recipient in recipients)recipient.Certificate.Dispose();throw;}
    }
}
