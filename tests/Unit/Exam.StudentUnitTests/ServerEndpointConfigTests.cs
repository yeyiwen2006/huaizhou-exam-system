using System.IO;
using Exam.Student.Core;

namespace Exam.StudentUnitTests;

/// <summary>
/// B07 部署配置（server.json）解析：文件缺失返回 (null, null) 由界面提示缺配置；
/// 文件存在时字段级严格校验，错误信息面向监考人员可直接行动。
/// </summary>
public sealed class ServerEndpointConfigTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("exam-server-config-").FullName;

    [Fact]
    public void Load_MissingFile_ReturnsNullConfigWithoutError()
    {
        var (config, error) = ServerEndpointConfig.Load(_directory);

        Assert.Null(config);
        Assert.Null(error);
    }

    [Fact]
    public void Load_ValidFileWithThumbprint_ParsesAndNormalizes()
    {
        WriteConfig("""
            {
              "formatVersion": 1,
              "host": "192.168.1.10",
              "port": 5001,
              "certificateSha256": "AB:CD:EF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC"
            }
            """);

        var (config, error) = ServerEndpointConfig.Load(_directory);

        Assert.Null(error);
        Assert.NotNull(config);
        Assert.Equal("192.168.1.10", config.Host);
        Assert.Equal(5001, config.Port);
        Assert.Equal("abcdef00112233445566778899aabbccddeeff00112233445566778899aabbcc", config.CertificateSha256);
    }

    [Fact]
    public void Load_ValidFileWithoutThumbprint_AllowsStandardChainValidation()
    {
        WriteConfig("""{ "formatVersion": 1, "host": "10.0.0.8", "port": 443 }""");

        var (config, error) = ServerEndpointConfig.Load(_directory);

        Assert.Null(error);
        Assert.NotNull(config);
        Assert.Equal("10.0.0.8", config.Host);
        Assert.Equal(443, config.Port);
        Assert.Null(config.CertificateSha256);
    }

    [Theory]
    [InlineData("""{ "formatVersion": 2, "host": "192.168.1.10", "port": 5001 }""", "只支持")]
    [InlineData("""{ "host": "192.168.1.10", "port": 5001 }""", "formatVersion")]
    [InlineData("""{ "formatVersion": 1, "port": 5001 }""", "host")]
    [InlineData("""{ "formatVersion": 1, "host": "exam.example", "port": 5001 }""", "IP 地址")]
    [InlineData("""{ "formatVersion": 1, "host": "192.168.1.10" }""", "port")]
    [InlineData("""{ "formatVersion": 1, "host": "192.168.1.10", "port": 0 }""", "1–65535")]
    [InlineData("""{ "formatVersion": 1, "host": "192.168.1.10", "port": 65536 }""", "1–65535")]
    [InlineData("""{ "formatVersion": 1, "host": "192.168.1.10", "port": 5001, "certificateSha256": "zz" }""", "64 个十六进制字符")]
    [InlineData("""{ "formatVersion": 1, "host": "192.168.1.10", "port": 5001, "certificateSha256": "" }""", "certificateSha256")]
    [InlineData("""not-json""", "JSON")]
    [InlineData("""[1,2,3]""", "JSON 对象")]
    public void Load_InvalidContent_ReturnsActionableError(string content, string expectedFragment)
    {
        WriteConfig(content);

        var (config, error) = ServerEndpointConfig.Load(_directory);

        Assert.Null(config);
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error.Message);
        Assert.Contains(ServerEndpointConfig.FileName, error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]  // 63 字符
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeff")] // 65 字符
    public void NormalizeThumbprint_InvalidInput_ReturnsNull(string? value)
    {
        Assert.Null(ServerEndpointConfig.NormalizeThumbprint(value!));
    }

    [Fact]
    public void NormalizeThumbprint_AcceptsSpacingAndCaseVariants()
    {
        var spaced = "AB CD EF 00 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF 00 11 22 33 44 55 66 77 88 99 AA BB CC";
        Assert.Equal(
            "abcdef00112233445566778899aabbccddeeff00112233445566778899aabbcc",
            ServerEndpointConfig.NormalizeThumbprint(spaced));
    }

    [Fact]
    public void Load_DirectoryRequired()
    {
        Assert.Throws<ArgumentException>(() => ServerEndpointConfig.Load(" "));
    }

    private void WriteConfig(string content) =>
        File.WriteAllText(Path.Combine(_directory, ServerEndpointConfig.FileName), content);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* 临时目录清理失败不影响断言 */ }
    }
}
