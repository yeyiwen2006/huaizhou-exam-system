using Exam.Contracts.Administration;
using Exam.Domain.Administration;

namespace Exam.UnitTests.Administration;

public sealed class AdministrationRulesTests
{
    [Theory]
    [InlineData("", "001", "00001")]
    [InlineData("合成考生", "1a", "00001")]
    [InlineData("合成考生", "１", "00001")]
    [InlineData("合成考生", "001", "1 2")]
    [InlineData("合成\n考生", "001", "00001")]
    public void InvalidRows_AreReportedWithoutProducingWrites(string name,string classText,string admission)
    {
        var plan=RosterRules.Plan([], [new(1,name,classText,admission)]);
        Assert.NotEmpty(plan.Issues);Assert.Empty(plan.NewRows);
    }
    [Fact]
    public void IdenticalDuplicatesAreSkipped_ButConflictingNumbersRejectWholeBatch()
    {
        var existing=new CandidateRosterEntry("person","合成考生","001","00001");
        var plan=RosterRules.Plan([existing],[new(1," 合成考生 ","001","00001"),new(2,"另一合成考生","001","00002")]);
        Assert.Empty(plan.Issues);Assert.Equal(1,plan.Duplicates);
        Assert.Equal("00002",Assert.Single(plan.NewRows).AdmissionNo);
        var conflict=RosterRules.Plan([existing],[new(1,"新考生","001","00002"),new(2,"不同姓名","001","00001")]);
        Assert.Single(conflict.Issues);Assert.Equal(2,conflict.Issues[0].RowNumber);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(10001)]
    public void ImportSizeIsBounded(int size)
    {
        var plan=RosterRules.Plan([],Enumerable.Range(1,size).Select(i=>new RosterImportRow(i,"合成考生","001",i.ToString())).ToArray());
        Assert.NotEmpty(plan.Issues);
    }
    [Fact]
    public void DuplicateRowNumbersAndWithinBatchIdentityConflictsAreRejected()
    {
        Assert.NotEmpty(RosterRules.Plan([], [new(1,"甲","001","01"),new(1,"乙","001","02")]).Issues);
        Assert.NotEmpty(RosterRules.Plan([], [new(1,"甲","001","01"),new(2,"乙","001","01")]).Issues);
    }
    [Theory]
    [InlineData(0,false,false,DeviceConnectionState.Online)]
    [InlineData(134,false,false,DeviceConnectionState.Online)]
    [InlineData(135,false,false,DeviceConnectionState.Offline)]
    [InlineData(136,false,false,DeviceConnectionState.Offline)]
    [InlineData(1,true,false,DeviceConnectionState.Offline)]
    [InlineData(1,false,true,DeviceConnectionState.Recovering)]
    public void ConnectionStatusUsesExactServerDeadline(int seconds,bool disconnected,bool recovering,DeviceConnectionState expected)
    {
        var seen=new DateTimeOffset(2026,10,2,0,0,0,TimeSpan.Zero);
        Assert.Equal(expected,ConnectionStatusPolicy.Evaluate(seen.AddSeconds(seconds),seen,disconnected?seen:null,recovering));
    }
    [Fact]
    public void NoCommunicationIsUnknown_NotAnInventedOnlineState()=>
        Assert.Equal(DeviceConnectionState.Unknown,ConnectionStatusPolicy.Evaluate(DateTimeOffset.UtcNow,null,null,false));
}
