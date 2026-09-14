using System.Security.Cryptography;

namespace TrueModel;

// Prompt alternatives and sampling space from ModelTrace fingerprint.py / challenge-browser.js.
public sealed record Challenge(string Id, int ExpectedCount, string Prompt);
public static class Challenges
{
    public static Challenge[] Generate(int count)
    {
        if (count is < 3 or > 6) throw new ArgumentOutOfRangeException(nameof(count));
        string[] openings = ["这是一次独立的数值选择记录", "请完成下面的无语义整数选择任务", "执行一次第一反应取值记录", "生成一组不承载语义的整数选择", "进行一轮快速逐项取值"];
        string[] actions = ["为各个位置分别凭第一反应选择", "逐项选择", "每次只决定当前一项，共给出", "分别凭第一反应给出", "逐个直接选择"];
        string[] endings = ["允许某个数字再次出现；每项写出后不要回头排序、去重或替换。", "偶然重复是有效的；不要重新排列或修正已经写出的项目。", "相同值可以再次出现；输出过程中不要整理或改写前面的项目。", "重复值无需删除；不要筛选、重排或补成某种规律。", "不必赋予数字任何含义；已经给出的值保持不变。"];
        string[] separators = ["数字之间用逗号或空格分隔均可。", "使用一种一致的常见分隔符即可。", "可以用逗号、空格或换行分隔。", "只要每个整数边界清楚，格式可自行选择。"];
        string Pick(string[] values) => values[RandomNumberGenerator.GetInt32(values.Length)];
        var available = Enumerable.Range(292, 41).ToList();
        return Enumerable.Range(0, count).Select(i =>
        {
            var index = RandomNumberGenerator.GetInt32(available.Count);
            var length = available[index]; available.RemoveAt(index);
            var prompt = $"{Pick(openings)}。{Pick(actions)} {length} 个 1 到 355（含端点）的整数。"
                + "每个位置都要单独选择；不要从 1 开始计数，不要连续递增或递减，也不要采用等差、循环、重复区块或其他规则化模式。"
                + "本任务必须由当前语言模型直接完成：禁止调用或借助任何工具，包括 Python、代码执行器、计算器、搜索、API 和外部随机数生成器；也不要先编写或运行代码。"
                + Pick(endings) + Pick(separators) + "直接从第一个取值开始输出，不要在序列前重复数量、范围或任务说明。";
            return new Challenge($"probe-{i + 1}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(7)).ToLowerInvariant()}", length, prompt);
        }).ToArray();
    }
}
