namespace Sgs.Collector.Decode;

// MemberPath 为 null 表示这个成员被跳过（只记警告，不算失败）
public sealed record MemberAssignment(DecodedMember Member, string? MemberPath, string? Warning);

// 实现 D5：成员 key 在第一次出现时确定并永久固定，之后即使扩展名推断结果变了也不改
internal static class MemberKeyAssigner
{
    private const string DuplicateWarning = "成员路径重复，已跳过：";
    private const string ConflictWarning = "成员路径冲突，已跳过";
    private const string RenameConflictWarning = "改名冲突，使用原始路径：";

    // existing：source_path -> 成员路径（数据库里该包已有成员的 key 去掉“包 key + /”之后的部分）
    // members：只包含 Error == null 的成员；返回结果与 members 同序
    public static IReadOnlyList<MemberAssignment> Assign(IReadOnlyDictionary<string, string> existing, IReadOnlyList<DecodedMember> members)
    {
        // reserved 包含包里已有的全部成员路径（含本次没有出现的成员），新成员不能占用
        var reserved = new HashSet<string>(existing.Values, StringComparer.Ordinal);
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        // 本次已分配成功的 source_path：数据库对 (parent_id, source_path) 有唯一索引，同一个 source_path 只能落一行
        var assignedSources = new HashSet<string>(StringComparer.Ordinal);
        var results = new MemberAssignment?[members.Count];

        // 第一轮：已有映射的成员沿用原路径。claimed 加不进去只可能是包内有重复条目
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            if (!existing.TryGetValue(member.SourcePath, out var existingPath))
            {
                continue;
            }

            results[i] = claimed.Add(existingPath) && assignedSources.Add(member.SourcePath)
                ? new MemberAssignment(member, existingPath, null)
                : new MemberAssignment(member, null, DuplicateWarning + member.SourcePath);
        }

        // 第二轮：未改名的成员优先占用自己的路径
        for (var i = 0; i < members.Count; i++)
        {
            var member = members[i];
            if (results[i] is not null || !string.Equals(member.SuggestedPath, member.SourcePath, StringComparison.Ordinal))
            {
                continue;
            }

            if (assignedSources.Contains(member.SourcePath))
            {
                results[i] = new MemberAssignment(member, null, DuplicateWarning + member.SourcePath);
                continue;
            }

            if (!reserved.Contains(member.SourcePath) && claimed.Add(member.SourcePath))
            {
                assignedSources.Add(member.SourcePath);
                results[i] = new MemberAssignment(member, member.SourcePath, null);
            }
            else
            {
                results[i] = new MemberAssignment(member, null, ConflictWarning);
            }
        }

        // 第三轮：改名的成员先试推断名，被占用就退回原始路径
        for (var i = 0; i < members.Count; i++)
        {
            if (results[i] is not null)
            {
                continue;
            }

            var member = members[i];
            if (assignedSources.Contains(member.SourcePath))
            {
                results[i] = new MemberAssignment(member, null, DuplicateWarning + member.SourcePath);
                continue;
            }

            if (!reserved.Contains(member.SuggestedPath) && claimed.Add(member.SuggestedPath))
            {
                assignedSources.Add(member.SourcePath);
                results[i] = new MemberAssignment(member, member.SuggestedPath, null);
            }
            else if (!reserved.Contains(member.SourcePath) && claimed.Add(member.SourcePath))
            {
                assignedSources.Add(member.SourcePath);
                results[i] = new MemberAssignment(member, member.SourcePath, RenameConflictWarning + member.SourcePath);
            }
            else
            {
                results[i] = new MemberAssignment(member, null, ConflictWarning);
            }
        }

        // 三轮之后每个位置都已赋值
        return Array.ConvertAll(results, result => result!);
    }
}
