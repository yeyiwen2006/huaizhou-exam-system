using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Exam.Contracts.Administration;
using Exam.Contracts.Common;
using Exam.Contracts.Exams;
using Exam.Contracts.Submissions;
using Exam.Contracts.Versioning;
using Exam.Domain.Exams;

internal static partial class ExamCli
{
    /// <summary>原始的 --error-format 是否要求 JSON 错误格式（该模式下 stderr 不能被实时提醒污染）。</summary>
    internal static bool IsJsonErrorFormat(string[] arguments)
    {
        for(var index=0;index<arguments.Length-1;index++)
            if(arguments[index]=="--error-format")return arguments[index+1]=="json";
        return false;
    }

    private static async Task<int> RunAdministrationAsync(string category,string[] arguments,CancellationToken ct)
    {
        var firstOption=Array.FindIndex(arguments,x=>x.StartsWith("--",StringComparison.Ordinal));
        if(firstOption<0)firstOption=arguments.Length;
        var positional=arguments[..firstOption];var options=ParseOptions(arguments[firstOption..]);
        if(positional.Length==0)return UnknownCommand([category]);
        if(!TryResolveServer(options.Server??Environment.GetEnvironmentVariable(ServerEnvironment),out var address))
        {Console.Error.WriteLine("请提供有效的 HTTPS 服务地址。");return 2;}
        using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){BaseAddress=address,Timeout=TimeSpan.FromMinutes(2)};
        var json=JsonDefaults.Create();string? logicalServer=null;
        async Task<T> Send<T>(HttpMethod method,string path,object? body=null)where T:class
        {
            using var message=new HttpRequestMessage(method,path);AddCredentials(message);
            if(body is not null)message.Content=JsonContent.Create(body,options:json);
            using var response=await http.SendAsync(message,ct);
            // 先按状态码判定：错误响应常常是网关或重启页的非 JSON 正文，
            // 解析它会抛 JsonException 被当成输入错误（退出 2），重试与日志都会因此走偏。
            if(!response.IsSuccessStatusCode)throw new ExamCliException(response.StatusCode,"服务器未完成操作。");
            var envelope=await response.Content.ReadFromJsonAsync<ApiEnvelope<T>>(json,ct);
            if(envelope?.Error is not null||envelope?.Result is null)
                throw new ExamCliException(response.StatusCode,envelope?.Error?.Message??"服务器未完成操作。");
            if(string.IsNullOrWhiteSpace(envelope.ServerId)||envelope.ServerEpoch<1||logicalServer is not null&&logicalServer!=envelope.ServerId)
                throw new InvalidDataException("响应服务器身份发生变化，停止当前操作。");
            logicalServer??=envelope.ServerId;return envelope.Result;
        }
        async Task<ExamSummary> FindExam(string name)=>
            (await Send<ExamSummary[]>(HttpMethod.Get,"/api/v1/exams"))
                .SingleOrDefault(x=>ExamSettingsRules.NameKey(x.Name)==ExamSettingsRules.NameKey(name))
                ??throw new ArgumentException("没有找到指定名称的考试。");
        async Task<RoomGroup> FindRoom(string name)=>
            (await Send<RoomGroup[]>(HttpMethod.Get,"/api/v1/rooms"))
                .SingleOrDefault(x=>x.Name.Normalize(NormalizationForm.FormKC).Equals(name.Trim().Normalize(NormalizationForm.FormKC),StringComparison.OrdinalIgnoreCase))
                ??throw new ArgumentException("没有找到指定名称的试场。");
        async Task<JsonDocument> Input()
        {
            if(options.Input is null)throw new ArgumentException("该操作需要 --input FILE|- 指定 UTF-8 JSON。");
            if(options.Input!="-"&&new FileInfo(Path.GetFullPath(options.Input)).Length>16*1024*1024)
                throw new ArgumentException("JSON 输入超过16 MiB，请拆分名册批次。");
            var text=options.Input=="-"?await Console.In.ReadToEndAsync(ct):await File.ReadAllTextAsync(Path.GetFullPath(options.Input),Encoding.UTF8,ct);
            if(text.Length>16*1024*1024)throw new ArgumentException("JSON 输入过大。");return JsonDocument.Parse(text);
        }
        string RequestId(JsonElement? input=null)=>options.RequestId??
            (input is {ValueKind:JsonValueKind.Object} root&&root.TryGetProperty("requestId",out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null)
            ??Guid.NewGuid().ToString("N");
        static long Expected(JsonElement input,long fallback)=>input.ValueKind==JsonValueKind.Object&&input.TryGetProperty("expectedVersion",out var value)?value.GetInt64():fallback;
        async Task<DeviceDirectoryEntry[]> AllDevices(string? room=null,string? query=null)
        {
            var rows=new List<DeviceDirectoryEntry>();int? offset=0;
            while(offset is not null)
            {
                var path=$"/api/v1/devices?offset={offset}&limit=2000"+(room is null?"":"&roomGroupId="+Uri.EscapeDataString(room))+(query is null?"":"&query="+Uri.EscapeDataString(query));
                var page=await Send<DeviceDirectoryPage>(HttpMethod.Get,path);rows.AddRange(page.Items);
                if(page.NextOffset is { } next&&next<=offset)throw new InvalidDataException("设备分页游标未前进。");
                if(rows.Count>100000)throw new InvalidDataException("设备数量超出本次命令允许范围，请按试场筛选。");offset=page.NextOffset;
            }
            return rows.ToArray();
        }
        // --error-format json 时 stderr 是机器读取的单个错误信封，实时提醒会污染它：
        // 该模式下提醒改走成功诊断缓冲（退出码 0 时输出），实时信号由 stdout 的 JSON 事件承担。
        var streamReminders=!IsJsonErrorFormat(Environment.GetCommandLineArgs());
        // 读取一批收齐事件并按名单版本发出，读到没有新事件为止。
        // 文本模式下可读提醒直接实时写 stderr：长时运行的 follow 必须即时可见，
        // 且不能因最终退出码非零而被错误层的缓冲丢弃。
        async Task DrainCollectionAsync(ExamSummary exam,CollectionWatch watch)
        {
            while(true)
            {
                var before=watch.Cursor;
                var page=await Send<CollectionCompletedNotification[]>(HttpMethod.Get,
                    $"/api/v1/submissions/collection/events?examId={Uri.EscapeDataString(exam.ExamId)}&afterRosterVersion={watch.Cursor}");
                foreach(var item in watch.Accept(exam.ExamId,page))
                {
                    // 事件写 stdout（一行一个，便于脚本消费）；可读提醒写 stderr。
                    Console.WriteLine(JsonSerializer.Serialize(item,json));
                    var reminder=$"本场考试全部答卷已成功回收：{item.ParticipantCount} 名实际参试考生（名单版本 {item.RosterVersion}，{item.CompletedAt:yyyy-MM-dd HH:mm:ss}）。";
                    if(streamReminders)Console.Error.Write(reminder+Environment.NewLine);
                    else WriteSuccessDiagnostic(reminder);
                }
                Console.Out.Flush();
                if(page.Length==0||watch.Cursor==before)return;
            }
        }
        // 可重试的收齐轮询失败：整页校验拒绝（游标未前进）、网络异常、本机请求超时与 5xx/限流。
        // 取消（Ctrl+C 或 --timeout-seconds 到点）不算可重试失败，交给外层按约定退出。
        bool IsRetryableWatchFailure(Exception error)=>error switch
        {
            InvalidOperationException=>true,
            HttpRequestException=>true,
            TaskCanceledException when !ct.IsCancellationRequested=>true,
            ExamCliException{Status:HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests}=>true,
            ExamCliException{Status:var status}=>(int)status>=500,
            _=>false,
        };
        // 只写本地判定的原因：服务器正文绝不能进终端（错误层就是为此存在的）。
        static string DescribeWatchFailure(Exception error)=>error switch
        {
            InvalidOperationException=>"服务器返回的收齐事件无效，本页未采用。",
            TaskCanceledException=>"请求超时。",
            HttpRequestException=>"无法连接服务器。",
            ExamCliException{Status:var status}=>$"服务器暂时不可用（{(int)status}）。",
            _=>"未预期的错误。",
        };
        try
        {
            object result;var valid=true;
            switch(category,positional)
            {
                case ("roster",["list",var examName]):
                    result=await Send<CandidateRosterSnapshot>(HttpMethod.Get,$"/api/v1/exams/{Uri.EscapeDataString((await FindExam(examName)).ExamId)}/roster");break;
                case ("roster",["import",var examName]):
                {
                    var exam=await FindExam(examName);var path=$"/api/v1/exams/{Uri.EscapeDataString(exam.ExamId)}/roster";
                    var current=await Send<CandidateRosterSnapshot>(HttpMethod.Get,path);using var input=await Input();
                    var element=input.RootElement.ValueKind==JsonValueKind.Array?input.RootElement:input.RootElement.GetProperty("rows");
                    var rows=element.Deserialize<RosterImportRow[]>(json)??throw new ArgumentException("缺少 rows 数组。");
                    rows=rows.Select((row,index)=>row with{RowNumber=row.RowNumber>0?row.RowNumber:index+1}).ToArray();
                    var imported=await Send<RosterImportResult>(HttpMethod.Post,path+"/import",new ImportCandidateRosterRequest(RequestId(input.RootElement),Expected(input.RootElement,current.Version),rows));
                    valid=imported.Applied;result=imported;break;
                }
                case ("roster",[var action,var examName,var admission])when action is "update" or "delete":
                {
                    var exam=await FindExam(examName);var path=$"/api/v1/exams/{Uri.EscapeDataString(exam.ExamId)}/roster";
                    var current=await Send<CandidateRosterSnapshot>(HttpMethod.Get,path);var person=current.Entries.SingleOrDefault(x=>x.AdmissionNo==admission)
                        ??throw new ArgumentException("当前考试名单中没有该准考证号。");
                    if(action=="delete")result=await Send<CandidateRosterSnapshot>(HttpMethod.Delete,path+"/"+Uri.EscapeDataString(person.CandidateId),new DeleteCandidateRosterRequest(RequestId(),current.Version));
                    else
                    {
                        using var input=await Input();var rowElement=input.RootElement.TryGetProperty("entry",out var entry)?entry:input.RootElement;
                        var row=rowElement.Deserialize<RosterImportRow>(json)??throw new ArgumentException("缺少更正后的姓名、班级和准考证号。");
                        result=await Send<CandidateRosterSnapshot>(HttpMethod.Put,path+"/"+Uri.EscapeDataString(person.CandidateId),
                            new UpdateCandidateRosterRequest(RequestId(input.RootElement),Expected(input.RootElement,current.Version),row with{RowNumber=Math.Max(1,row.RowNumber)}));
                    }
                    break;
                }
                case ("rooms",["list"]):result=await Send<RoomGroup[]>(HttpMethod.Get,"/api/v1/rooms");break;
                case ("rooms",["create",var name]):result=await Send<RoomGroup>(HttpMethod.Post,"/api/v1/rooms",new CreateRoomGroupRequest(RequestId(),name));break;
                case ("rooms",["rename",var oldName,var newName]):
                {
                    var room=await FindRoom(oldName);result=await Send<RoomGroup>(HttpMethod.Put,"/api/v1/rooms/"+Uri.EscapeDataString(room.GroupId),new RenameRoomGroupRequest(RequestId(),room.Version,newName));break;
                }
                case ("rooms",["delete",var name]):
                {
                    var room=await FindRoom(name);result=await Send<DeletedRoomGroup>(HttpMethod.Delete,"/api/v1/rooms/"+Uri.EscapeDataString(room.GroupId),new DeleteRoomGroupRequest(RequestId(),room.Version));break;
                }
                case ("rooms",["move",var name]):
                {
                    string? group=name=="未分组"?null:(await FindRoom(name)).GroupId;
                    using var input=await Input();var numbers=input.RootElement.GetProperty("clientNumbers").Deserialize<string[]>(json)??throw new ArgumentException("缺少学生端编号列表。");
                    if(numbers.Length is <1 or >2000||numbers.Distinct(StringComparer.Ordinal).Count()!=numbers.Length)throw new ArgumentException("请选择1至2000个不同的学生端编号。");
                    var reason=input.RootElement.GetProperty("reason").GetString()??throw new ArgumentException("缺少转组原因。");
                    var available=(await AllDevices()).ToDictionary(x=>x.ClientNumber,StringComparer.Ordinal);
                    var expected=input.RootElement.TryGetProperty("expectedVersions",out var versions)?versions.Deserialize<Dictionary<string,long>>(json):null;
                    var targets=numbers.Select(number=>available.TryGetValue(number,out var d)?new DeviceMoveTarget(number,expected?.GetValueOrDefault(number,d.Version)??d.Version):throw new ArgumentException("部分学生端编号不存在，未发送转组请求。")).ToArray();
                    result=await Send<MoveDevicesResult>(HttpMethod.Post,"/api/v1/rooms/move-devices",new MoveDevicesRequest(RequestId(input.RootElement),group,targets,reason));break;
                }
                case ("devices",["list"]):result=await AllDevices();break;
                case ("devices",["list",var name]):result=await AllDevices(name=="未分组"?"":(await FindRoom(name)).GroupId);break;
                case ("devices",["show",var number]):result=(await AllDevices(query:number)).SingleOrDefault(x=>x.ClientNumber==number)??throw new ArgumentException("没有该学生端编号。");break;
                case ("devices",["history",var number]):result=await Send<DeviceRoomHistory[]>(HttpMethod.Get,"/api/v1/devices/"+Uri.EscapeDataString(number)+"/room-history");break;
                case ("monitor",[var action,var examName])when action is "show" or "missing":
                {
                    var exam=await FindExam(examName);var path=$"/api/v1/exams/{Uri.EscapeDataString(exam.ExamId)}";
                    result=action=="show"?await Send<MonitoringSnapshot>(HttpMethod.Get,path+"/monitoring"):(object)await Send<MonitoringParticipant[]>(HttpMethod.Get,path+"/uncollected");break;
                }
                case ("monitor",["watch",var watchExam,.. var watchArguments]):
                {
                    // 单次读取并发出收齐事件后退出：可给位置参数指定起始名单版本（默认 0），
                    // 便于脚本只发出上次之后的新事件。
                    var startVersion=0L;
                    if(watchArguments.Length>1)return UnknownCommand(["monitor","watch",watchExam,..watchArguments]);
                    if(watchArguments.Length==1&&(!long.TryParse(watchArguments[0],out startVersion)||startVersion<0))
                        return UnknownCommand(["monitor","watch",watchExam,..watchArguments]);
                    await DrainCollectionAsync(await FindExam(watchExam),new CollectionWatch(startVersion));return 0;
                }
                case ("monitor",["follow",var followExam,.. var followArguments]):
                {
                    // 持续等待并发出；位置参数依次为：起始名单版本（默认 0）、间隔秒数（默认 20，1..3600）、轮数（默认不设上限）。
                    if(followArguments.Length>3)return UnknownCommand(["monitor","follow",followExam,..followArguments]);
                    var startVersion=0L;var interval=TimeSpan.FromSeconds(20);int? rounds=null;
                    if(followArguments.Length>0&&(!long.TryParse(followArguments[0],out startVersion)||startVersion<0))
                        return UnknownCommand(["monitor","follow",followExam,..followArguments]);
                    if(followArguments.Length>1)
                    {
                        if(!int.TryParse(followArguments[1],out var seconds)||seconds is <1 or >3600)
                            return UnknownCommand(["monitor","follow",followExam,..followArguments]);
                        interval=TimeSpan.FromSeconds(seconds);
                    }
                    if(followArguments.Length>2)
                    {
                        if(!int.TryParse(followArguments[2],out var parsed)||parsed<=0)
                            return UnknownCommand(["monitor","follow",followExam,..followArguments]);
                        rounds=parsed;
                    }
                    var followTarget=await FindExam(followExam);var watch=new CollectionWatch(startVersion);var completed=0;var failures=0;
                    do
                    {
                        var succeeded=false;
                        try
                        {
                            await DrainCollectionAsync(followTarget,watch);
                            failures=0;succeeded=true;
                        }
                        catch(Exception error)when(IsRetryableWatchFailure(error))
                        {
                            // 与考务端弹窗同口径：整页无效不前进游标、网络与 5xx 都继续等待重试；
                            // 鉴权或请求本身无效无法靠重试解决，交给外层按退出码约定失败。
                            failures++;
                            var note=$"收齐事件读取失败（连续第 {failures} 次）：{DescribeWatchFailure(error)}";
                            if(streamReminders)Console.Error.Write(note+Environment.NewLine);
                            else WriteSuccessDiagnostic(note);
                        }
                        // 只有成功读完一轮才算一轮：持续失败不得因为“轮数到点”而以 0 退出。
                        if(succeeded)completed++;
                        if(rounds is { } limit&&completed>=limit)return 0;
                        try{await Task.Delay(interval,ct);}
                        catch(OperationCanceledException){return 0;}
                    }
                    while(true);
                }
                case ("publications",["publish",var examName]):
                {
                    var exam=await FindExam(examName);using var input=await Input();var request=input.RootElement.Deserialize<PublishExamBundleApiRequest>(json)??throw new ArgumentException("缺少完整发布请求。");
                    result=await Send<ExamVersionSnapshot>(HttpMethod.Post,$"/api/v1/exams/{Uri.EscapeDataString(exam.ExamId)}/publications",request with{RequestId=RequestId(input.RootElement)});break;
                }
                default:return UnknownCommand([category,..arguments]);
            }
            Console.WriteLine(JsonSerializer.Serialize(result,json));return valid?0:2;
        }
        catch(ExamCliException error)
        {
            Console.Error.WriteLine(error.Message);return error.Status switch
            {HttpStatusCode.BadRequest=>2,HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden=>3,HttpStatusCode.ServiceUnavailable=>6,var code when (int)code>=500=>6,_=>4};
        }
        catch(Exception error)when(error is ArgumentException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {Console.Error.WriteLine("输入或响应无效："+error.Message);return 2;}
    }
}
