using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MessengerDesktop;

/// <summary>Shared keyboard-accessible picker for message actions and preferences.</summary>
public sealed class ReactionPicker : WrapPanel
{
    public event Action<string>? Picked;
    public ReactionPicker()
    {
        Orientation = Orientation.Horizontal;
        foreach (var emoji in ReactionCatalog.Main.Concat(ReactionCatalog.More))
        {
            var button = new Button
            {
                Tag = emoji, Classes = { "reactionChoice" },
                Content = new ReactionIcon { Emoji = emoji, Width = 24, Height = 24 }
            };
            AutomationProperties.SetName(button, ReactionCatalog.Label(emoji));
            ToolTip.SetTip(button, ReactionCatalog.Label(emoji));
            button.Click += (_, _) => Picked?.Invoke(emoji);
            Children.Add(button);
        }
    }

    public void Select(string? emoji)
    {
        foreach (var button in Children.OfType<Button>())
        {
            var selected = Equals(button.Tag, emoji);
            button.Classes.Set("chosen", selected);
            AutomationProperties.SetName(button, ReactionCatalog.Label((string)button.Tag!) + (selected ? ", выбрано" : ""));
        }
    }
}
