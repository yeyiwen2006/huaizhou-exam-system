using Exam.Infrastructure.Packaging;

namespace Exam.Admin.Services;

/// <summary>考务窗口和 CLI 使用同一个本地检查、预览与原子发布实现。</summary>
public sealed class ExamImportService(AdminApiClient api) : ImportPipeline(api);
