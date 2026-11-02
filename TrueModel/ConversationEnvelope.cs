using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace TrueModel;

public sealed record ConversationMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public static class ConversationEnvelope
{
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
}
