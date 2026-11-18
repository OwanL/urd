using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Urd.Core.Tools;

// Reads rendered document HTML into Markdown, keeping headings, paragraphs, tables and meaningful links.
internal static class WebPageExtractor {
  private const int MaxOutputBytes = 10 * 1024 * 1024;
  private const int MaxLinks = 500;
  private static readonly Regex whitespace = new("\\s+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
  private const string removalSelector =
    "script,style,noscript,template,iframe,object,canvas,svg,video,audio,form,button,input,select,textarea,label,header,nav,aside,footer,[aria-hidden=\"true\"],[hidden]";
  private const string blockSelector = "h1,h2,h3,h4,h5,h6,p,li,dt,dd,blockquote,pre,tr,hr,div,section";

  public static WebPageExtraction Extract(string html, string baseUrl) {
    IDocument document = new HtmlParser().ParseDocument(html);
    foreach (IElement noisy in document.QuerySelectorAll(removalSelector))
      noisy.Remove();
    IElement? root = document.QuerySelector("main") ?? document.QuerySelector("article") ?? document.Body;
    if (root is null)
      return new WebPageExtraction("", false);

    var captured = new HashSet<IElement>();
    var builder = new StringBuilder();
    long bytes = 0;
    int links = 0;
    bool truncated = false;

    foreach (IElement element in root.QuerySelectorAll(blockSelector)) {
      if (captured.Contains(element))
        continue;
      if (element.TagName is "DIV" or "SECTION" or "ARTICLE" && !IsLeafContainer(element))
        continue;
      string text = BlockText(element);
      if (text.Length == 0)
        continue;
      string block = element.TagName switch {
        "H1" => "# " + text,
        "H2" => "## " + text,
        "H3" => "### " + text,
        "H4" => "#### " + text,
        "H5" => "##### " + text,
        "H6" => "###### " + text,
        "LI" or "DT" or "DD" => "- " + text,
        "PRE" => "```\n" + text + "\n```",
        "HR" => "---",
        _ => text
      };
      if (builder.Length > 0)
        builder.Append("\n\n");
      builder.Append(block);
      bytes += Encoding.UTF8.GetByteCount(block) + 2;
      if (bytes > MaxOutputBytes) {
        truncated = true;
        break;
      }
    }
    return new WebPageExtraction(builder.ToString(), truncated);

    // Standalone text containers; containers holding blocks are reduced to their blocks instead.
    bool IsLeafContainer(IElement element) =>
      element.QuerySelector(blockSelector) is null;

    string BlockText(IElement element) {
      string raw = element.TagName switch {
        "TR" => string.Join(" | ", element.QuerySelectorAll("th,td")
          .Select(cell => Collapse(InlineText(cell))).Where(cell => cell.Length > 0)),
        "PRE" => element.TextContent.Trim(),
        _ => Collapse(InlineText(element))
      };
      return raw.Trim();
    }

    string InlineText(IElement element) {
    var inline = new StringBuilder();
      Append(element);
      return inline.ToString();

      void Append(IElement current) {
        foreach (INode node in current.ChildNodes) {
          if (node is IText text) {
            inline.Append(text.Data);
          } else if (node is IElement child) {
            (string Label, string Href)? anchor = Anchor(child);
            if (anchor is { } link)
              inline.Append(CultureInfo.InvariantCulture, $"[{link.Label}]({link.Href})");
            else
              Append(child);
          }
        }
      }
    }

    (string Label, string Href)? Anchor(IElement element) {
      if (element.TagName != "A")
        return null;
      string raw = element.GetAttribute("href") ?? "";
      if (raw.Length == 0 || raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
          raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || links >= MaxLinks)
        return null;
      string href = Resolve(baseUrl, raw);
      string label = Collapse(element.TextContent);
      if (label.Length == 0 || !href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        return null;
      links++;
      return (label, href);
    }
  }

  private static string Resolve(string baseUri, string href) {
    if (Uri.TryCreate(href, UriKind.Absolute, out Uri? absolute))
      return absolute.ToString();
    if (Uri.TryCreate(baseUri, UriKind.Absolute, out Uri? root) && Uri.TryCreate(root, href, out Uri? resolved))
      return resolved.ToString();
    return href;
  }

  private static string Collapse(string text) => whitespace.Replace(text, " ").Trim();
}

internal sealed record WebPageExtraction(string Markdown, bool Truncated);
