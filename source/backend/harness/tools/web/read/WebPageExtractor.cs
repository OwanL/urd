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

    var builder = new StringBuilder();
    var inline = new StringBuilder();
    long bytes = 0;
    int links = 0;
    bool truncated = false;

    Walk(root, root.TagName, null);
    Flush(root.TagName, null);
    return new WebPageExtraction(builder.ToString(), truncated);

    // Flush inline runs on either side of nested blocks, rather than dropping the parent text.
    void Walk(IElement element, string blockTag, string? href) {
      foreach (INode node in element.ChildNodes) {
        if (truncated)
          return;
        if (node is IText text) {
          inline.Append(text.Data);
        } else if (node is IElement child) {
          if (IsBlock(child.TagName)) {
            Flush(blockTag, href);
            if (truncated)
              return;
            if (child.TagName is "PRE" or "TR") {
              string content = child.TagName == "PRE" ? child.TextContent.Trim() :
                string.Join(" | ", child.QuerySelectorAll("th,td")
                  .Select(cell => Collapse(InlineText(cell))).Where(cell => cell.Length > 0));
              AddBlock(child.TagName, content, href);
            } else {
              Walk(child, child.TagName, href);
              Flush(child.TagName, href);
            }
          } else if (child.TagName == "A" && child.QuerySelector(blockSelector) is not null) {
            // Linked cards can wrap several blocks; keep both their structure and destination.
            Flush(blockTag, href);
            string? destination = Href(child);
            Walk(child, blockTag, destination);
            Flush(blockTag, destination);
          } else if (Anchor(child) is { } link) {
            inline.Append(CultureInfo.InvariantCulture, $"[{link.Label}]({link.Href})");
          } else {
            Walk(child, blockTag, href);
          }
        }
      }
    }

    void Flush(string tag, string? href) {
      string text = Collapse(inline.ToString());
      inline.Clear();
      if (!truncated)
        AddBlock(tag, text, href);
    }

    void AddBlock(string tag, string text, string? href) {
      if (text.Length == 0 || truncated)
        return;
      if (href is not null && tag != "PRE" && links < MaxLinks) {
        links++;
        text = $"[{text}]({href})";
      }
      string block = tag switch {
        "H1" => "# " + text,
        "H2" => "## " + text,
        "H3" => "### " + text,
        "H4" => "#### " + text,
        "H5" => "##### " + text,
        "H6" => "###### " + text,
        "LI" or "DT" or "DD" => "- " + text,
        "PRE" => "```\n" + text + "\n```",
        _ => text
      };
      if (builder.Length > 0)
        builder.Append("\n\n");
      builder.Append(block);
      bytes += Encoding.UTF8.GetByteCount(block) + 2;
      if (bytes > MaxOutputBytes)
        truncated = true;
    }

    string InlineText(IElement element) {
      var content = new StringBuilder();
      Append(element);
      return content.ToString();

      void Append(IElement current) {
        foreach (INode node in current.ChildNodes) {
          if (node is IText text) {
            content.Append(text.Data);
          } else if (node is IElement child) {
            (string Label, string Href)? anchor = Anchor(child);
            if (anchor is { } link)
              content.Append(CultureInfo.InvariantCulture, $"[{link.Label}]({link.Href})");
            else
              Append(child);
          }
        }
      }
    }

    (string Label, string Href)? Anchor(IElement element) {
      string? href = Href(element);
      if (href is null)
        return null;
      string label = Collapse(element.TextContent);
      if (label.Length == 0)
        return null;
      links++;
      return (label, href);
    }

    string? Href(IElement element) {
      if (element.TagName != "A" || links >= MaxLinks)
        return null;
      string raw = element.GetAttribute("href") ?? "";
      if (raw.Length == 0 || raw.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
          raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
        return null;
      string href = Resolve(baseUrl, raw);
      return href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : null;
    }
  }

  private static bool IsBlock(string tag) => tag is
    "H1" or "H2" or "H3" or "H4" or "H5" or "H6" or "P" or "LI" or "DT" or "DD" or
    "BLOCKQUOTE" or "PRE" or "TR" or "HR" or "DIV" or "SECTION";

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
