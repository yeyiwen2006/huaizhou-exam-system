namespace Exam.Infrastructure.Postgres;

public sealed class PostgresMigrationBlockedException() : InvalidOperationException(
    "考试正在进行或仍有等待、作答中的参加考试记录，禁止迁移数据库。请在考试及收卷完成后的维护窗口升级。");
