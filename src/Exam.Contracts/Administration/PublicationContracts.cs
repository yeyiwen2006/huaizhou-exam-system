namespace Exam.Contracts.Administration;

/// <summary>
/// 整体发布已生成且经实际解密、结构校验的学生包。身份只从已认证考务会话取得。
///
/// <paramref name="MaterialsSha256"/> 是**明文源材料归档**的 SHA-256（64 位十六进制），
/// 与 <see cref="UploadExamBundleRequest.MaterialsEnvelope"/> 里的密文一起提交；
/// 服务器按收到的字节重算并比对，不信任声明。它同时进入发布请求指纹，
/// 因此同一 RequestId 重试但材料内容不同会被判为幂等冲突，而不是静默接受。
/// </summary>
public sealed record PublishExamBundleApiRequest(string RequestId,long ExpectedPaperVersion,long ExpectedGradingVersion,
    string ResourceId,string ManifestJson,string AnswerKeyJson,IReadOnlyList<string> ChangedQuestionIds,
    string Reason,bool PreviewApproved,string MaterialsSha256="");

/// <summary>由服务器校验层构造；指纹包含明文学生包与源材料归档的哈希，但回执不含规则、答案或解锁密钥。</summary>
public sealed record PublishExamBundleRequest(string ExamId,string RequestId,long ExpectedPaperVersion,long ExpectedGradingVersion,
    string ArtifactId,string PackageContentSha256,string ManifestJson,string AnswerKeyJson,
    IReadOnlyList<string> ChangedQuestionIds,string Operator,string Reason,string MaterialsSha256="");

/// <summary>仅包含用于加密的公开证书；绝不导出私钥或未包装的数据密钥。</summary>
public sealed record PublicationRecipient(string NodeId,string CertificateDerBase64);
public sealed record PublicationRecipients(IReadOnlyList<PublicationRecipient> Recipients);
public sealed record PublicationPackageEnvelope(string PackageId,long PaperVersion,int ChunkSize,string NoncePrefix,
    IReadOnlyDictionary<string,string> WrappedKeys,IReadOnlyDictionary<string,string> CertificateThumbprints);

/// <summary>
/// 上传表单的两份密文：`package` 是学生包，`materials` 是源材料归档。
/// 两份都用受管节点公钥加密后再上传——临时缓冲里不能出现题目明文或独立答案。
/// 归档信封随 metadata 一起提交：解密所需的标识与版本绑定在密文的附加认证数据里，必须与加密时一致。
/// </summary>
public sealed record UploadExamBundleRequest(PublishExamBundleApiRequest Publication,PublicationPackageEnvelope Envelope,
    PublicationPackageEnvelope? MaterialsEnvelope=null);
