using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MessengerDesktop;

/// <summary>Shared keyboard-accessible picker for message actions and preferences.</summary>
public sealed class ReactionPicker : WrapPanel
{
    public event Action<string>? Picked;
    public ReactionPicker() : this(false) { }

    public ReactionPicker(bool compact)
    {
        Orientation = Orientation.Horizontal;
        Button? more = null;
        if (compact)
        {
            more = new Button { Classes = { "reactionChoice" } };
            SetMoreLabel(more, false);
        }
        foreach (var emoji in ReactionCatalog.Main.Concat(ReactionCatalog.More))
        {
            if (more != null && Children.Count == ReactionCatalog.Main.Count) Children.Add(more);
            var button = new Button
            {
                Tag = emoji, Classes = { "reactionChoice" },
                Content = new ReactionIcon { Emoji = emoji, Width = 24, Height = 24 },
                IsVisible = !compact || ReactionCatalog.Main.Contains(emoji)
            };
            AutomationProperties.SetName(button, ReactionCatalog.Label(emoji));
            ToolTip.SetTip(button, ReactionCatalog.Label(emoji));
            button.Click += (_, _) => Picked?.Invoke(emoji);
            Children.Add(button);
        }
        if (more == null) return;
        more.Click += (_, e) =>
        {
            var extra = Children.OfType<Button>().Where(button => button.Tag is string emoji && ReactionCatalog.More.Contains(emoji)).ToArray();
            var expanded = !extra[0].IsVisible;
            foreach (var button in extra) button.IsVisible = expanded;
            SetMoreLabel(more, expanded);
            if (expanded) extra[0].Focus();
            e.Handled = true;
        };
    }

    static void SetMoreLabel(Button button, bool expanded)
    {
        var label = expanded ? "Скрыть дополнительные реакции" : "Больше реакций";
        button.Content = new Glyph
        {
            Data = (Geometry)Application.Current!.FindResource(expanded ? "IcChevronUp" : "IcPlus")!, Width = 18, Height = 18
        };
        AutomationProperties.SetName(button, label);
        ToolTip.SetTip(button, label);
    }

    public void Select(string? emoji)
    {
        foreach (var button in Children.OfType<Button>().Where(candidate => candidate.Tag is string))
        {
            var selected = Equals(button.Tag, emoji);
            button.Classes.Set("chosen", selected);
            AutomationProperties.SetName(button, ReactionCatalog.Label((string)button.Tag!) + (selected ? ", выбрано" : ""));
        }
    }
}
