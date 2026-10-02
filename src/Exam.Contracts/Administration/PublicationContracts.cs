namespace Exam.Contracts.Administration;

/// <summary>整体发布已生成且经实际解密、结构校验的学生包。身份只从已认证考务会话取得。</summary>
public sealed record PublishExamBundleApiRequest(string RequestId,long ExpectedPaperVersion,long ExpectedGradingVersion,
    string ResourceId,string ManifestJson,string AnswerKeyJson,IReadOnlyList<string> ChangedQuestionIds,
    string Reason,bool PreviewApproved);

/// <summary>由服务器校验层构造；指纹包含明文学生包的哈希，但回执不含规则、答案或解锁密钥。</summary>
public sealed record PublishExamBundleRequest(string ExamId,string RequestId,long ExpectedPaperVersion,long ExpectedGradingVersion,
    string ArtifactId,string PackageContentSha256,string ManifestJson,string AnswerKeyJson,
    IReadOnlyList<string> ChangedQuestionIds,string Operator,string Reason);

/// <summary>仅包含用于加密的公开证书；绝不导出私钥或未包装的数据密钥。</summary>
public sealed record PublicationRecipient(string NodeId,string CertificateDerBase64);
public sealed record PublicationRecipients(IReadOnlyList<PublicationRecipient> Recipients);
public sealed record PublicationPackageEnvelope(string PackageId,long PaperVersion,int ChunkSize,string NoncePrefix,
    IReadOnlyDictionary<string,string> WrappedKeys,IReadOnlyDictionary<string,string> CertificateThumbprints);
public sealed record UploadExamBundleRequest(PublishExamBundleApiRequest Publication,PublicationPackageEnvelope Envelope);
