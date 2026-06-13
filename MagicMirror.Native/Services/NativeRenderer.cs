using HtmlAgilityPack;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace MagicMirror.Native.Services;

/// <summary>Cepha NativeRenderer — converts MvcEngine HTML to native MAUI controls.</summary>
public sealed class NativeRenderer
{
    public event EventHandler<string>? NavigationRequested;
    public event EventHandler<(string Action, Dictionary<string, string> Data)>? FormSubmitted;

    public View Render(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var root = new VerticalStackLayout { Padding = 0, Spacing = 0, BackgroundColor = Color.FromArgb("#0D1117") };
        foreach (var child in doc.DocumentNode.ChildNodes)
        {
            var view = MapNode(child);
            if (view != null) root.Children.Add(view);
        }
        return root;
    }

    private View? MapNode(HtmlNode node)
    {
        if (node.NodeType == HtmlNodeType.Text)
        {
            var text = HtmlEntity.DeEntitize(node.InnerText).Trim();
            if (string.IsNullOrWhiteSpace(text)) return null;
            return new Label { Text = text, TextColor = Color.FromArgb("#E6EDF3"), FontSize = 14 };
        }
        if (node.NodeType != HtmlNodeType.Element) return null;
        var tag = node.Name.ToLowerInvariant();
        if (tag is "h1" or "h2" or "h3") return new Label { Text = HtmlEntity.DeEntitize(node.InnerText).Trim(), FontSize = tag == "h1" ? 28 : tag == "h2" ? 24 : 20, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#E6EDF3") };
        if (tag == "a") { var btn = new Button { Text = HtmlEntity.DeEntitize(node.InnerText).Trim(), BackgroundColor = Color.FromArgb("#238636"), TextColor = Colors.White, CornerRadius = 6 }; btn.Clicked += (_, _) => NavigationRequested?.Invoke(this, node.GetAttributeValue("href", "/")); return btn; }
        var layout = new VerticalStackLayout { Spacing = 4 };
        foreach (var c in node.ChildNodes) { var v = MapNode(c); if (v != null) layout.Children.Add(v); }
        return layout.Children.Count > 0 ? layout : null;
    }
}