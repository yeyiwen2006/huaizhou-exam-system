using System.Net;
using Exam.Domain.Exams;
using Exam.Infrastructure.Packaging;

internal static partial class ExamCli
{
    /// <summary>本地检查与预览使用和 GUI 相同的流水线；正式发布只调用既有加密包原子上传 API。</summary>
    private static async Task<int> RunImportRemoteAsync(string action, string source, string[] arguments,
        CancellationToken cancellationToken)
    {
        string? name = null, output = null, server = null, reason = null;
        var changed = new List<string>();
        var confirmed = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index++)
        {
            var key = arguments[index];
            // --changed 可重复出现（逐题声明），其余选项只允许一次。
            if (key != "--changed" && !seen.Add(key)) { Console.Error.WriteLine("不能重复指定选项：" + key); return 2; }
            if (key == "--confirm") { confirmed = true; continue; }
            if (key is not ("--exam" or "--output" or "--server" or "--reason" or "--changed") ||
                index + 1 >= arguments.Length || arguments[index + 1].StartsWith("--", StringComparison.Ordinal))
            { Console.Error.WriteLine("未知选项或缺少取值：" + key); return 2; }
            var value = arguments[++index];
            switch (key)
            {
                case "--exam": name = value; break;
                case "--output": output = value; break;
                case "--server": server = value; break;
                case "--reason": reason = value; break;
                case "--changed":
                    changed.AddRange(value.Split([',', '，', '、', ';', '；', ' '], StringSplitOptions.RemoveEmptyEntries
                        | StringSplitOptions.TrimEntries));
                    break;
            }
        }
        if (action == "preview" && (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(output) || confirmed))
        { Console.Error.WriteLine("生成预览需要 --exam 考试名称 和 --output 新预览目录，不能同时确认发布。"); return 2; }
        if (action == "publish" && (!confirmed || output is not null))
        { Console.Error.WriteLine("请先逐题核对已保存的预览，再使用 --confirm 确认发布；发布不能重新指定输出目录。"); return 2; }
        if (action == "publish" && (reason is not null || changed.Count > 0))
        { Console.Error.WriteLine("发布只重放已保存的预览：修改原因与变更题号请在 preview 时给出。"); return 2; }
        if (action == "materials" && (confirmed || output is null || reason is not null || changed.Count > 0))
        { Console.Error.WriteLine("取回源材料需要 考试名称 与 --output 新目录；不能确认发布或声明变更题号。"); return 2; }
        if (!TryResolveServer(server ?? Environment.GetEnvironmentVariable(ServerEnvironment), out var address)) return 2;
        if (string.IsNullOrWhiteSpace(OperatorToken()))
        { Console.Error.WriteLine("请先登录考务账号，再检查或发布试卷。"); return 3; }
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { BaseAddress = address, Timeout = TimeSpan.FromMinutes(30) };
        var api = new HttpExamPublicationClient(http, AddCredentials);
        try
        {
            if (action == "preview")
            {
                if (Directory.Exists(output) || File.Exists(output))
                    throw new IOException("预览输出目录已经存在，请选择新的目录。");
                var matches = (await api.ListExamsAsync(cancellationToken))
                    .Where(x => ExamSettingsRules.NameKey(x.Name) == ExamSettingsRules.NameKey(name!)).ToArray();
                if (matches.Length != 1) throw new ArgumentException("没有找到唯一的同名考试，请核对考试名称。");
                using var prepared = await new ImportPipeline(api).PrepareAsync(source, matches[0].ExamId,
                    cancellationToken, reason, changed.Count == 0 ? null : changed);
                await prepared.ExportAsync(output!, cancellationToken);
                Console.WriteLine($"检查通过：{matches[0].Name}，{prepared.Questions.Count} 道题。尚未发布。");
                if (prepared.IsRevision)
                {
                    // 差异提示按内容算出，用来帮操作者确认；服务器会用同一算法核对声明。
                    Console.WriteLine("本次修订：" + (prepared.ChangedQuestionsHint.Count == 0
                        ? "没有内容差异"
                        : "内容有变化的是 " + string.Join("、", prepared.ChangedQuestionsHint)) + "。");
                    Console.WriteLine("将声明的变更题号：" + string.Join("、", prepared.DeclaredChangedQuestions) +
                        "（服务器会按内容核对，不一致会拒绝发布）。");
                    Console.WriteLine("本次修改原因：" + prepared.Reason);
                }
                Console.WriteLine("预览已保存到：" + Path.GetFullPath(output!));
                Console.WriteLine("该目录包含敏感材料，仅向当前账户开放。请逐题打开 PDF、核对参考答案和选项绑定后再确认发布。");
                foreach (var q in prepared.Questions)
                    Console.WriteLine($"第 {q.QuestionNo} 题：{q.Subject} / {q.Group} / {q.Type}，满分 {q.Score}，{q.PageCount} 页，选项 {q.Options}。");
                return 0;
            }
            if (action == "materials")
            {
                if (!string.IsNullOrWhiteSpace(name) &&
                    ExamSettingsRules.NameKey(name) != ExamSettingsRules.NameKey(source))
                    throw new ArgumentException("位置上的考试名称与 --exam 不一致。");
                var matches = (await api.ListExamsAsync(cancellationToken))
                    .Where(x => ExamSettingsRules.NameKey(x.Name) == ExamSettingsRules.NameKey(source)).ToArray();
                if (matches.Length != 1) throw new ArgumentException("没有找到唯一的同名考试，请核对考试名称。");
                var snapshot = await api.GetExamAsync(matches[0].ExamId, cancellationToken);
                if (snapshot.Versions.PaperVersion < 1)
                    throw new InvalidDataException("这场考试还没有已发布的试卷版本，没有可取回的源材料。");
                var archive = await api.DownloadPaperMaterialsAsync(matches[0].ExamId, snapshot.Versions.PaperVersion,
                    cancellationToken);
                var read = PaperMaterialsArchive.Read(archive);
                if (!read.Succeeded) throw new InvalidDataException(read.Failure?.Value ?? "服务器返回的归档不可用。");
                await PaperMaterialsArchive.ExtractAsync(read, output!, cancellationToken);
                Console.WriteLine($"已取回：{matches[0].Name} 试卷版本 {snapshot.Versions.PaperVersion}，" +
                    $"共 {read.Manifest!.Files.Count} 个材料文件。");
                Console.WriteLine("材料目录：" + Path.GetFullPath(output!));
                Console.WriteLine("该目录含题目源文件与独立答案，仅向当前账户开放；改完后用 import preview 重新检查。");
                return 0;
            }

            using var preview = await PreparedExamImport.LoadAsync(source, api, cancellationToken);
            if (name is not null && ExamSettingsRules.NameKey(name) != ExamSettingsRules.NameKey(preview.ExamName))
                throw new ArgumentException("所确认的考试名称与保存的预览不一致。");
            var result = await new ImportPipeline(api).PublishAsync(preview, cancellationToken);
            Console.WriteLine($"正式发布成功：{preview.ExamName}，试卷版本 {result.Versions.PaperVersion}，规则版本 {result.Versions.GradingVersion}。");
            Console.WriteLine("此回执不代表所有学生端均已更新，请在设备监控中核对。预览与原请求已保留，重复执行不会创建第二份试卷。");
            return 0;
        }
        catch (ExamPublicationApiException error)
        {
            Console.Error.WriteLine(error.Message);
            if (action == "publish") Console.Error.WriteLine("未确认发布成功。请保留预览目录，连接原服务器后用同一目录重试。");
            return error.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? 3 :
                (int)error.StatusCode >= 500 ? 6 : 4;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(action == "publish"
                ? "发布已中止或超时，结果尚未确认。请保留原预览目录，用同一目录重试。"
                : "检查已取消或超时，未发布。");
            return cancellationToken.IsCancellationRequested ? 130 : 5;
        }
        catch (HttpRequestException)
        {
            Console.Error.WriteLine("网络请求失败，未确认发布成功；请保留原预览目录，不要重新生成另一份发布请求。");
            return 5;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or InvalidDataException or IOException or
            UnauthorizedAccessException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or FormatException)
        {
            Console.Error.WriteLine("检查或发布未完成：" + error.Message);
            return 4;
        }
    }
}
