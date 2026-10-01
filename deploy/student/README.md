# 学生端发布包（B07）

本目录包含学生端的发布配置、部署配置模板与冒烟测试脚本。发布产物是一个自包含的
文件夹，拷贝到全新目标设备即可用普通账户运行。

## 目标环境

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 19041 及以上 / Windows 11，x64 |
| 账户 | 普通用户账户，**无需管理员权限** |
| .NET | **无需安装**任何 SDK 或运行时（自包含随包携带） |
| TeX | 无需安装（试题在考务端编译成 PDF 题包下发） |
| 证书 | 无需在学生机安装服务器证书（见下方“信任配置”） |
| 显示 | 100% / 125% / 150% 缩放下界面清晰（PerMonitorV2） |

## 发布

在仓库根目录执行（需要 .NET 10 SDK，见 `global.json`）：

```powershell
powershell -ExecutionPolicy Bypass -File deploy\student\publish-student.ps1
```

- 产物：`artifacts\student\win-x64\publish\`（文件夹）与 `Exam.Student-win-x64.zip`
- 脚本内置布局校验：`asInvoker`、`PerMonitorV2`、自包含运行时（WPF 程序集、
  hostpolicy）、原生 SQLite（`e_sqlite3.dll`）、不预置 `server.json` 与本地数据库
- 构建溯源写入 `build-info.json`（UTC 时间、SDK 版本、提交号）；无法用 git 取
  提交号时可显式传 `-Commit <sha>`
- 锁文件说明：自包含发布按锁定版本解析（还原会临时给锁文件加 win-x64 段，
  脚本在发布完成后立即恢复仓库原锁文件，保证 CI 的无 RID 锁定还原不受影响）

## 部署到考试机

1. 把 `publish` 文件夹（或 zip 解压结果）拷贝到目标机任意可写位置，例如
   `D:\ExamStudent`。
2. 复制 `server.example.json` 为 `server.json`，填写实际服务器地址：

   ```json
   {
     "formatVersion": 1,
     "host": "服务器IP",
     "port": 5001,
     "certificateSha256": "服务器HTTPS证书SHA-256指纹（可选）"
   }
   ```

3. 双击 `Exam.Student.exe` 运行。

`server.json` 必须放在 exe 同目录。应用启动时读取：

- **文件有效**：激活页自动填入服务器地址（界面提示“来自本机部署配置”）；
- **文件缺失**：激活页显示警示，联系监考人员部署，也可手动输入地址应急；
- **文件无效**（格式/字段错误）：激活页显示具体错误原因，可手动输入应急；
- 手动输入在三种情况下都保留，作为部署遗漏时的应急通道。

## 信任配置（证书指纹 pin）

服务器以 HTTPS 提供服务。学校内网通常使用自签证书，学生机默认不信任它。两种处理方式：

1. **推荐：指纹 pin**。在 `server.json` 填写 `certificateSha256`（证书 SHA-256
   指纹，64 位十六进制，允许空格/冒号分隔）。学生端 TLS 握手时只信任与该指纹
   完全一致的服务器证书，指纹不符立即拒绝连接并明确提示“服务器不受信任”。
   无需在学生机安装证书，无需管理员权限。
2. 不填 `certificateSha256` 时使用 Windows 标准证书链校验（适合服务器证书由
   域/机构 CA 签发的环境）。

在**服务器机器**上读取 Kestrel 所用证书的 SHA-256 指纹：

```powershell
$cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Thumbprint -eq '<Kestrel配置的证书缩略图>' }
[System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash($cert.RawData)) -replace '-',''
```

## 冒烟测试（发布产物）

```powershell
powershell -ExecutionPolicy Bypass -File deploy\student\smoke-test-student.ps1
```

对发布产物执行三个隔离启动场景（无配置 / 有效配置 / 无效配置，用户数据目录
隔离到临时目录），断言主窗口正常创建、干净关闭退出码为 0，并复查 DPI 清单与
自包含布局。首次运行新构建的 exe 可能被 Windows Defender 拖慢，超时先重跑一次
再下结论。

## 恢复用例在发布包重现

发布包与仓库代码同源，恢复/交卷/更新等行为由学生端测试套件覆盖（含真实 SQLite
落盘）；在发布机上执行：

```powershell
dotnet test tests\Unit\Exam.StudentUnitTests -c Release
```

（发布机需要 SDK 才能跑测试；只做考试部署的机器无需此步骤。）S16 的整机恢复
演练（退出恢复、换账号、全新部署恢复）以本发布包为对象，由 T 阶段执行并记录。

## 文件清单

| 文件 | 用途 |
| --- | --- |
| `publish-student.ps1` | 自包含发布 + 布局校验 + 溯源 + 打包 |
| `smoke-test-student.ps1` | 发布产物隔离启动冒烟（三场景） |
| `server.example.json` | 部署配置模板（占位值，不含真实地址/凭据） |
| `README.md` | 本说明（随包复制，供监考/部署人员查阅） |
