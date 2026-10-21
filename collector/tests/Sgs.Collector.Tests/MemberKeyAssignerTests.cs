using Sgs.Collector.Decode;

namespace Sgs.Collector.Tests;

public sealed class MemberKeyAssignerTests
{
    // ---------- 第一轮：已有映射 ----------

    [Fact]
    public void Assign_ExistingMappingWinsOverTheCurrentSuggestion()
    {
        // 第一次被推断为 .json 之后，即使现在推断为 .txt，key 也不变（D5）
        var member = Member("a.sgs", "a.txt");

        var result = Assert.Single(MemberKeyAssigner.Assign(Existing(("a.sgs", "a.json")), [member]));

        Assert.Same(member, result.Member);
        Assert.Equal("a.json", result.MemberPath);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Assign_SecondEntryWithTheSameSourcePathAsAnExistingMemberIsSkipped()
    {
        var first = Member("a.sgs", "a.json");
        var duplicate = Member("a.sgs", "a.json");

        var result = MemberKeyAssigner.Assign(Existing(("a.sgs", "a.json")), [first, duplicate]);

        Assert.Equal("a.json", result[0].MemberPath);
        Assert.Null(result[0].Warning);
        Assert.Null(result[1].MemberPath);
        Assert.Equal("成员路径重复，已跳过：a.sgs", result[1].Warning);
    }

    // ---------- 第二轮：未改名的成员 ----------

    [Fact]
    public void Assign_FirstSeenMembersUseTheirSuggestedPath()
    {
        var result = MemberKeyAssigner.Assign(
            Existing(),
            [Member("a.sgs", "a.json"), Member("img/b.png"), Member("c.sgs", "c.proto")]);

        Assert.Equal(new[] { "a.json", "img/b.png", "c.proto" }, result.Select(r => r.MemberPath));
        Assert.All(result, r => Assert.Null(r.Warning));
    }

    [Fact]
    public void Assign_UnchangedMemberCannotTakeAPathThatAnotherMemberAlreadyHolds()
    {
        // old.sgs 本次没有出现，但它占用的 x.json 仍然保留：不能被新成员占用
        var existing = Existing(("old.sgs", "x.json"));

        var result = MemberKeyAssigner.Assign(existing, [Member("x.json")]);

        var only = Assert.Single(result);
        Assert.Null(only.MemberPath);
        Assert.Equal("成员路径冲突，已跳过", only.Warning);
    }

    [Fact]
    public void Assign_SecondUnchangedMemberWithTheSameNewPathIsSkipped()
    {
        var result = MemberKeyAssigner.Assign(Existing(), [Member("a.json"), Member("a.json")]);

        Assert.Equal("a.json", result[0].MemberPath);
        Assert.Null(result[0].Warning);
        Assert.Null(result[1].MemberPath);
        Assert.Equal("成员路径冲突，已跳过", result[1].Warning);
    }

    // ---------- 第三轮：改名的成员 ----------

    [Fact]
    public void Assign_UnchangedMemberBeatsRenamedMemberAndTheRenamedOneFallsBackToItsSourcePath()
    {
        // 改名的成员排在前面：先处理的是未改名者，a.json 归它
        var renamed = Member("a.sgs", "a.json");
        var unchanged = Member("a.json");

        var result = MemberKeyAssigner.Assign(Existing(), [renamed, unchanged]);

        Assert.Same(renamed, result[0].Member);
        Assert.Equal("a.sgs", result[0].MemberPath);
        Assert.Equal("改名冲突，使用原始路径：a.sgs", result[0].Warning);
        Assert.Same(unchanged, result[1].Member);
        Assert.Equal("a.json", result[1].MemberPath);
        Assert.Null(result[1].Warning);
    }

    [Fact]
    public void Assign_RenamedMemberCannotTakeAReservedSuggestedPathAndUsesItsSourcePath()
    {
        var existing = Existing(("old.sgs", "x.json"));

        var result = Assert.Single(MemberKeyAssigner.Assign(existing, [Member("x.sgs", "x.json")]));

        Assert.Equal("x.sgs", result.MemberPath);
        Assert.Equal("改名冲突，使用原始路径：x.sgs", result.Warning);
    }

    [Fact]
    public void Assign_RenamedMemberIsSkippedWhenBothItsSuggestedAndSourcePathAreReserved()
    {
        var existing = Existing(("old1", "a.json"), ("old2", "a.sgs"));

        var result = Assert.Single(MemberKeyAssigner.Assign(existing, [Member("a.sgs", "a.json")]));

        Assert.Null(result.MemberPath);
        Assert.Equal("成员路径冲突，已跳过", result.Warning);
    }

    [Fact]
    public void Assign_RenamedMembersWithTheSameSuggestionAreServedInInputOrder()
    {
        var result = MemberKeyAssigner.Assign(Existing(), [Member("a.sgs", "a.json"), Member("a.SGS", "a.json")]);

        Assert.Equal("a.json", result[0].MemberPath);
        Assert.Null(result[0].Warning);
        Assert.Equal("a.SGS", result[1].MemberPath);
        Assert.Equal("改名冲突，使用原始路径：a.SGS", result[1].Warning);
    }

    // ---------- 整体 ----------

    [Fact]
    public void Assign_ReturnsOneResultPerMemberInInputOrderAcrossAllRounds()
    {
        var renamedNew = Member("n.sgs", "n.json"); // 第三轮
        var existingOne = Member("e.sgs", "e.txt"); // 第一轮
        var unchangedNew = Member("u.png"); // 第二轮
        var members = new[] { renamedNew, existingOne, unchangedNew };

        var result = MemberKeyAssigner.Assign(Existing(("e.sgs", "e.json")), members);

        Assert.Equal(members, result.Select(r => r.Member));
        Assert.Equal(new[] { "n.json", "e.json", "u.png" }, result.Select(r => r.MemberPath));
    }

    [Fact]
    public void Assign_KeepsKeysStableWhenTheSamePackageIsAssignedAgain()
    {
        var members = new[] { Member("a.sgs", "a.json"), Member("a.json"), Member("b.sgs", "b.json") };
        var first = MemberKeyAssigner.Assign(Existing(), members);

        // 第二轮把第一轮的结果作为已有映射，推断结果即使变了也沿用原 key
        var existing = Existing(first.Where(r => r.MemberPath is not null).Select(r => (r.Member.SourcePath, r.MemberPath!)).ToArray());
        var changed = new[] { Member("a.sgs", "a.txt"), Member("a.json"), Member("b.sgs", "b.js") };
        var second = MemberKeyAssigner.Assign(existing, changed);

        Assert.Equal(first.Select(r => r.MemberPath), second.Select(r => r.MemberPath));
        Assert.All(second, r => Assert.Null(r.Warning));
    }

    [Fact]
    public void Assign_ReturnsAnEmptyListForNoMembers()
    {
        Assert.Empty(MemberKeyAssigner.Assign(Existing(("a.sgs", "a.json")), []));
    }

    // ---------- 辅助 ----------

    private static DecodedMember Member(string sourcePath, string? suggestedPath = null) =>
        new(sourcePath, suggestedPath ?? sourcePath, [1], null);

    private static Dictionary<string, string> Existing(params (string SourcePath, string MemberPath)[] items) =>
        items.ToDictionary(item => item.SourcePath, item => item.MemberPath, StringComparer.Ordinal);
}
