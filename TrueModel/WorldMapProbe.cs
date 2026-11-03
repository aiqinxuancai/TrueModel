using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace TrueModel;

// Community "redraw the world map" degradation probe: https://linux.do/t/topic/2980148
// The model writes a rule-based Python script; we run it and keep the 0/1 matrix.
public static class WorldMapProbe
{
    public const int Rows = 90;
    public const int Columns = 180;
    public const int Scale = 8;
    public const string MatrixFile = "world_map_matrix.csv";
    public const int HistoryLimit = 20;

    // Only the task body is whitespace-randomized; the delivery note carries
    // exact file names and the code-fence contract, which must stay intact.
    public static string CreatePrompt(string language)
    {
        var translation = PromptLanguages.Get(language);
        return PromptLanguages.RandomizeWhitespace(translation.WorldMap) + "\n\n" + translation.WorldMapDelivery;
    }

    private static readonly Regex Fence = new(@"```[ \t]*([A-Za-z0-9_+-]*)[^\n]*\n(.*?)```", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    public static string? ExtractScript(string response)
    {
        var blocks = Fence.Matches(response).Select(m => (Language: m.Groups[1].Value.ToLowerInvariant(), Code: m.Groups[2].Value.Trim())).ToArray();
        var labeled = blocks.Where(b => b.Language is "python" or "py" or "python3").Select(b => b.Code).ToArray();
        var candidates = labeled.Length > 0 ? labeled
            : blocks.Where(b => b.Language == "" && Regex.IsMatch(b.Code, @"^\s*(?:import|from)\s+\w", RegexOptions.Multiline)).Select(b => b.Code).ToArray();
        if (candidates.Length > 0) return candidates.MaxBy(c => c.Length);
        // Some models omit the fence entirely and reply with bare source.
        var text = response.Trim();
        return blocks.Length == 0 && Regex.IsMatch(text, @"^(?:#!|#|import\s|from\s|""""""|''')") && Regex.IsMatch(text, @"^\s*(?:import|from)\s+\w", RegexOptions.Multiline) ? text : null;
    }

    public static byte[] ParseMatrix(string csv)
    {
        var lines = csv.TrimStart((char)0xFEFF).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var count = lines.Length;
        while (count > 0 && string.IsNullOrWhiteSpace(lines[count - 1])) count--;
        if (count != Rows) throw new FormatException($"矩阵应为 {Rows} 行，实际为 {count} 行");
        var cells = new byte[Rows * Columns];
        for (var row = 0; row < Rows; row++)
        {
            var values = lines[row].Split(',');
            if (values.Length != Columns) throw new FormatException($"第 {row + 1} 行应有 {Columns} 个值，实际为 {values.Length} 个");
            for (var column = 0; column < Columns; column++)
                cells[row * Columns + column] = values[column].Trim() switch
                {
                    "0" => 0, "1" => 1,
                    var value => throw new FormatException($"第 {row + 1} 行第 {column + 1} 列的值“{(value.Length > 20 ? value[..20] + "…" : value)}”不是 0 或 1")
                };
        }
        return cells;
    }

    public static string FormatMatrix(byte[] cells) => string.Join('\n', Enumerable.Range(0, Rows)
        .Select(row => string.Join(',', cells.Skip(row * Columns).Take(Columns)))) + "\n";

    // White is land, black is water; nearest-neighbour upscaling only.
    public static byte[] EncodePng(byte[] cells, int scale = 1)
    {
        int width = Columns * scale, height = Rows * scale;
        var raw = new byte[(width + 1) * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                raw[y * (width + 1) + 1 + x] = cells[y / scale * Columns + x / scale] == 1 ? (byte)255 : (byte)0;
        using var output = new MemoryStream();
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // 8-bit grayscale, no interlace
        Chunk(output, "IHDR", header);
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);
            Chunk(output, "IDAT", compressed.ToArray());
        }
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        var name = Encoding.ASCII.GetBytes(type);
        output.Write(name);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32(name, data));
        output.Write(number);
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = 0xFFFFFFFF;
        foreach (var value in type.Concat(data)) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFF;
    }
}
