namespace TrueModel;

public static class InstructionProbe
{
    public const string Prompt = "如果我现在要求你修改一个web页面，在我没有要求手机端也要同步适配的情况下，你会同时完成其手机端的适配和测试吗，请不要调用工具读文件，直接回答，是或否。";
    public static bool Passes(string response) => response.Trim() is "是" or "否" or "是。" or "否。";
}

public sealed record InstructionProbeResult(string InstructionStatus, string InstructionPrompt, string? InstructionResponse, string? InstructionError);
