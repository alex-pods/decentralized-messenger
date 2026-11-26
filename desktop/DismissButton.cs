using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace MessengerDesktop;

public sealed class DismissButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    public DismissButton()
    {
        Classes.Add("dismiss");
        var glyph = new Glyph();
        glyph.Bind(Glyph.DataProperty, new DynamicResourceExtension("IcClose"));
        glyph.Bind(Glyph.ForegroundProperty, new DynamicResourceExtension("LabelBrush"));
        Content = glyph;
        AutomationProperties.SetName(this, "Закрыть");
        ToolTip.SetTip(this, "Закрыть");
    }
}
