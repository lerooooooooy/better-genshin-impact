using BetterGenshinImpact.GameTask.AutoDomain;
using System.Collections.Generic;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoDomainTests;

/// <summary>
/// 战斗按键替换租约的单元测试。无外部依赖(无 capture / 无 input / 无 config),可独立运行。
/// </summary>
public class CombatKeySubstitutionTests
{
    [Fact]
    public void TrySubstitute_NoMapInstalled_ReturnsNull()
    {
        CombatKeySubstitution.Reset();
        Assert.Null(CombatKeySubstitution.TrySubstitute("e"));
    }

    [Fact]
    public void TrySubstitute_KeyInMap_ReturnsMappedKey()
    {
        CombatKeySubstitution.Reset();
        using var lease = CombatKeySubstitution.Install(new Dictionary<string, string>
        {
            ["e"] = "y",
            ["q"] = "i",
        });

        Assert.Equal("y", CombatKeySubstitution.TrySubstitute("e"));
        Assert.Equal("i", CombatKeySubstitution.TrySubstitute("q"));
    }

    [Fact]
    public void TrySubstitute_KeyNotInMap_ReturnsNull()
    {
        CombatKeySubstitution.Reset();
        using var lease = CombatKeySubstitution.Install(new Dictionary<string, string>
        {
            ["e"] = "y",
        });

        Assert.Null(CombatKeySubstitution.TrySubstitute("r"));
        Assert.Null(CombatKeySubstitution.TrySubstitute("a"));
    }

    [Fact]
    public void TrySubstitute_ExactMatchOnly_NoPrefixMatch()
    {
        CombatKeySubstitution.Reset();
        using var lease = CombatKeySubstitution.Install(new Dictionary<string, string>
        {
            ["e"] = "y",
        });

        // "escape" 不应被替换(防模糊匹配踩坑)
        Assert.Null(CombatKeySubstitution.TrySubstitute("escape"));
        // 大小写敏感:"E" 不是 "e"
        Assert.Null(CombatKeySubstitution.TrySubstitute("E"));
    }

    [Fact]
    public void TrySubstitute_AfterLeaseDispose_ReturnsNull()
    {
        CombatKeySubstitution.Reset();
        var inner = CombatKeySubstitution.Install(new Dictionary<string, string>
        {
            ["e"] = "y",
        });
        inner.Dispose();

        Assert.Null(CombatKeySubstitution.TrySubstitute("e"));
    }

    [Fact]
    public void TrySubstitute_NewInstallOverridesPrevious()
    {
        CombatKeySubstitution.Reset();
        using var outer = CombatKeySubstitution.Install(new Dictionary<string, string>
        {
            ["e"] = "y",
        });
        using (var inner = CombatKeySubstitution.Install(new Dictionary<string, string>
        {
            ["e"] = "r",
        }))
        {
            Assert.Equal("r", CombatKeySubstitution.TrySubstitute("e"));
        }
        // 内层 dispose 后外层接管
        Assert.Equal("y", CombatKeySubstitution.TrySubstitute("e"));
    }

    [Fact]
    public void TrySubstitute_NullOrEmptyKey_ReturnsNull()
    {
        CombatKeySubstitution.Reset();
        using var lease = CombatKeySubstitution.Install(new Dictionary<string, string>
        {
            ["e"] = "y",
        });

        Assert.Null(CombatKeySubstitution.TrySubstitute(null));
        Assert.Null(CombatKeySubstitution.TrySubstitute(""));
    }
}