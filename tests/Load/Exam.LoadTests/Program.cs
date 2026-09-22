if (args is ["--help"] or ["-h"] or ["help"])
{
    Console.WriteLine("Exam.LoadTests 是 E05 负载场景入口。");
    Console.WriteLine("正式运行必须显式提供目标地址、合成账号集、场景和报告目录；不得默认连接生产考试。");
    return 0;
}

Console.Error.WriteLine("尚未提供负载场景参数。当前 M0 入口不会生成或伪造性能通过结果。");
return 2;
