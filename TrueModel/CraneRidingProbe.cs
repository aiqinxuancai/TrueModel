using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;

namespace TrueModel;

// A red-crowned-crane variant of https://simonwillison.net/2024/Oct/25/pelicans-on-a-bicycle/
// Success means usable SVG, not an automatic assessment of drawing quality or model identity.
public static class CraneRidingProbe
{
    public const int HistoryLimit = 20;
    public const int MaxStoredText = 400_000;
    private static readonly XNamespace SvgNamespace = "http://www.w3.org/2000/svg";
    private static readonly HashSet<string> Elements = new(StringComparer.Ordinal)
    {
        "svg", "g", "defs", "title", "desc", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "text", "tspan", "textPath", "use", "symbol", "clipPath", "mask", "marker", "pattern", "linearGradient",
        "radialGradient", "stop", "style", "filter", "feBlend", "feColorMatrix", "feComponentTransfer", "feComposite",
        "feConvolveMatrix", "feDiffuseLighting", "feDisplacementMap", "feDistantLight", "feDropShadow", "feFlood",
        "feFuncA", "feFuncB", "feFuncG", "feFuncR", "feGaussianBlur", "feMerge", "feMergeNode", "feMorphology",
        "feOffset", "fePointLight", "feSpecularLighting", "feSpotLight", "feTile", "feTurbulence"
    };
    private static readonly HashSet<string> DrawingElements = new(StringComparer.Ordinal)
        { "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text", "use" };

    public static string CreatePrompt(string language) => PromptLanguages.RandomizeWhitespace(PromptLanguages.Get(language).CraneRiding);

    public static string ExtractSvg(string response)
    {
        if (response.Length > MaxStoredText) throw new FormatException("SVG 回复过长，无法展示（最多 400,000 字符）");
        // Reject declarations even outside the extracted root, rather than dropping a DTD before parsing.
        if (Regex.IsMatch(response, @"<!\s*(?:DOCTYPE|ENTITY)\b", RegexOptions.IgnoreCase))
            throw new FormatException("SVG 包含不支持的文档类型或实体声明");
        var match = Regex.Match(response, @"<svg\b[\s\S]*</svg\s*>", RegexOptions.IgnoreCase);
        if (!match.Success) throw new FormatException("回复中未找到完整的 SVG，模型可能拒答或输出被截断");
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(match.Value), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxStoredText
            });
            document = XDocument.Load(reader);
        }
        catch (XmlException) { throw new FormatException("SVG 格式无效，无法解析，请查看模型回复"); }
        var root = document.Root!;
        if (root.Name.LocalName != "svg" || root.Name.Namespace != XNamespace.None && root.Name.Namespace != SvgNamespace)
            throw new FormatException("回复不是有效的 SVG 图像");
        var elements = root.DescendantsAndSelf().ToArray();
        if (elements.Length > 10_000 || elements.Any(e => e.Ancestors().Take(65).Count() > 64))
            throw new FormatException("SVG 结构过于复杂，无法展示");
        // Serve only static, self-contained drawing elements. Never insert model XML into the page DOM.
        foreach (var element in elements)
        {
            if (!Elements.Contains(element.Name.LocalName) || element.Name.Namespace != XNamespace.None && element.Name.Namespace != SvgNamespace)
            { element.Remove(); continue; }
            element.Name = SvgNamespace + element.Name.LocalName;
            foreach (var attribute in element.Attributes().ToArray())
            {
                var name = attribute.Name.LocalName;
                var value = attribute.Value.Trim();
                if (attribute.IsNamespaceDeclaration)
                {
                    if (name != "xmlns" || value != SvgNamespace.NamespaceName) attribute.Remove();
                    continue;
                }
                if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                    attribute.Name.Namespace != XNamespace.None && attribute.Name.NamespaceName != "http://www.w3.org/1999/xlink" ||
                    name is "src" || name == "href" && !Regex.IsMatch(value, @"^#[\w.:-]+$") || !IsLocalCss(value))
                    attribute.Remove();
            }
            if (element.Name.LocalName == "style" && !IsLocalCss(element.Value)) element.Remove();
        }
        foreach (var node in root.DescendantNodes().Where(n => n is XProcessingInstruction or XComment).ToArray()) node.Remove();
        if (!root.Descendants().Any(e => DrawingElements.Contains(e.Name.LocalName)))
            throw new FormatException("SVG 未包含可展示的绘图元素");
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static bool IsLocalCss(string value)
    {
        if (Regex.IsMatch(value, @"[@\\<>]|(?:https?|file|data|javascript)\s*:|//|(?:expression|image-set)\s*\(", RegexOptions.IgnoreCase)) return false;
        var withoutFragments = Regex.Replace(value, "url\\(\\s*['\"]?#[\\w.:-]+['\"]?\\s*\\)", "", RegexOptions.IgnoreCase);
        return !Regex.IsMatch(withoutFragments, @"url\s*\(", RegexOptions.IgnoreCase);
    }

    public static async Task Prune(AppDb db, CancellationToken token = default)
    {
        var models = await db.Models.Select(m => m.Id).ToArrayAsync(token);
        var rows = await db.CraneRides.AsNoTracking().Select(w => new { w.Id, w.ModelId, w.Status }).ToArrayAsync(token);
        var expired = rows.Where(w => !models.Contains(w.ModelId) && w.Status is not ("Queued" or "Running"))
            .Concat(rows.GroupBy(w => w.ModelId).SelectMany(g => g.OrderByDescending(w => w.Id).Skip(HistoryLimit)))
            .Select(w => w.Id).Distinct().ToArray();
        foreach (var chunk in expired.Chunk(500)) await db.CraneRides.Where(w => chunk.Contains(w.Id)).ExecuteDeleteAsync(token);
    }
}
