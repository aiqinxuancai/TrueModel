using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TrueModel;

public sealed record ConversationMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public static class ConversationEnvelope
{
    public const int MinimumSimulatedTurns = 64;
    // Roughly 15k tokens at four English characters per token; actual tokenization varies by model.
    public const int MinimumSimulatedCharacters = 60_000;
    private static readonly (string Topic, string Unit)[] ContextTopics =
    [
        ("notebook inventory", "notebooks"), ("library shelf inventory", "books"),
        ("reading progress", "pages"), ("garden container capacity", "litres"),
        ("walking distance", "metres"), ("practice session duration", "minutes"),
        ("packing box inventory", "items"), ("ribbon length", "centimetres"),
    ];

    // Pick a language first, then independently vary both sides of the greeting.
    // Keep the prelude neutral: it must not prescribe an answer or a language.
    private static readonly (string[] Users, string[] Assistants)[] Greetings =
    [
        (["你好", "嗨，你好！", "你好，我想问个问题。"],
         ["你好！有什么可以帮助你的吗？", "你好，请问有什么问题？", "嗨！很高兴与你交流，请讲。"]),
        (["Hello!", "Hi there.", "Hi, I have a question."],
         ["Hello! How can I help you?", "Hi! What would you like to ask?", "Hello, go ahead with your question."]),
        (["こんにちは。", "こんにちは！質問があります。", "どうも、こんにちは。"],
         ["こんにちは！何かお手伝いできますか？", "こんにちは。ご質問をどうぞ。", "こんにちは！どのようなご質問ですか？"]),
        (["안녕하세요.", "안녕하세요! 질문이 있어요.", "안녕하십니까?"],
         ["안녕하세요! 무엇을 도와드릴까요?", "안녕하세요. 질문해 주세요.", "반갑습니다! 어떤 질문이 있으신가요?"]),
        (["Bonjour !", "Salut !", "Bonjour, j’ai une question."],
         ["Bonjour ! Comment puis-je vous aider ?", "Bonjour, quelle est votre question ?", "Salut ! Je vous écoute."]),
        (["¡Hola!", "Hola, tengo una pregunta.", "¡Buenas!"],
         ["¡Hola! ¿En qué puedo ayudarte?", "Hola, ¿cuál es tu pregunta?", "¡Buenas! Adelante con tu pregunta."]),
        (["Hallo!", "Guten Tag!", "Hallo, ich habe eine Frage."],
         ["Hallo! Wie kann ich helfen?", "Guten Tag! Was möchten Sie fragen?", "Hallo, stellen Sie gerne Ihre Frage."]),
        (["Olá!", "Oi, tenho uma pergunta.", "Olá, tudo bem?"],
         ["Olá! Como posso ajudar?", "Oi! Qual é a sua pergunta?", "Olá, pode fazer sua pergunta."]),
    ];

    public static ConversationMessage[] Create(string prompt)
    {
        var greeting = Greetings[RandomNumberGenerator.GetInt32(Greetings.Length)];
        return
        [
            new("user", greeting.Users[RandomNumberGenerator.GetInt32(greeting.Users.Length)]),
            new("assistant", greeting.Assistants[RandomNumberGenerator.GetInt32(greeting.Assistants.Length)]),
            new("user", prompt),
        ];
    }

    public static ConversationMessage[] CreateSimulatedContext(string prompt)
    {
        var messages = Create(prompt)[..2].ToList();
        var characters = messages.Sum(m => m.Content.Length);
        // Generate complete, neutral U/A turns locally. Only the final question is sent for completion.
        while (messages.Count / 2 < MinimumSimulatedTurns || characters < MinimumSimulatedCharacters)
        {
            var (topic, unit) = ContextTopics[RandomNumberGenerator.GetInt32(ContextTopics.Length)];
            var values = Enumerable.Range(0, 6).Select(_ => RandomNumberGenerator.GetInt32(10, 1000)).ToArray();
            var batch = messages.Count / 2;
            var question = new StringBuilder($"Please check {topic} batch {batch}. Summarize each entry with a running total, then give the overall total, the smallest and largest values, and the values in ascending order. These are the six recorded entries:\n");
            var answer = new StringBuilder($"Here is the summary of the six entries in {topic} batch {batch}, measured in {unit}:\n");
            var total = 0;
            for (var i = 0; i < values.Length; i++)
            {
                question.AppendLine($"Entry {i + 1}: {values[i]} {unit}.");
                total += values[i];
                answer.AppendLine($"Entry {i + 1} contains {values[i]} {unit}. After including this entry, the running total for entries 1 through {i + 1} is {total} {unit}.");
            }
            answer.AppendLine($"The overall total is {total} {unit} across all six entries. The smallest recorded value is {values.Min()} {unit}, and the largest is {values.Max()} {unit}.");
            answer.Append($"In ascending order, the values are {string.Join(", ", values.Order())} {unit}. This completes the summary of this batch.");
            messages.Add(new("user", question.ToString()));
            messages.Add(new("assistant", answer.ToString()));
            characters += question.Length + answer.Length;
        }
        messages.Add(new("user", prompt));
        return messages.ToArray();
    }
}
