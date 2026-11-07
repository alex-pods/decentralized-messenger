using Avalonia.Controls;

namespace MessengerDesktop;

/// <summary>Ширина корпуса: 1100 сужает колонку, 760 складывает панели, 370 уменьшает циферблат.</summary>
static class ChassisWidth
{
    public static void Track(Control control)
    {
        void Apply(double width)
        {
            var stack = width < 760;
            var compact = width < 1100;
            control.Classes.Set("compact", compact);
            control.Classes.Set("stack", stack);
            control.Classes.Set("narrow", width < 480);

            if (control.FindControl<Grid>("ShellGrid") is { } shell)
            {
                shell.ColumnDefinitions = new ColumnDefinitions(stack ? "*" : compact ? "228,*" : "288,*");
                shell.RowDefinitions = new RowDefinitions(stack ? "Auto,*" : "*");
            }

            if (control.FindControl<Border>("ScreenBay") is { } bay)
            {
                Grid.SetColumn(bay, stack ? 0 : 1);
                Grid.SetRow(bay, stack ? 1 : 0);
            }

            if (control.FindControl<Grid>("LoginGrid") is { } login)
            {
                login.ColumnDefinitions = new ColumnDefinitions(stack ? "*" : compact ? "*,300" : "*,340");
                login.RowDefinitions = new RowDefinitions(stack ? "Auto,*" : "*");
            }

            if (control.FindControl<StackPanel>("LoginForm") is { } form)
            {
                Grid.SetColumn(form, stack ? 0 : 1);
                Grid.SetRow(form, stack ? 1 : 0);
            }
        }

        control.AttachedToVisualTree += (_, _) => Apply(control.Bounds.Width);
        control.SizeChanged += (_, e) => Apply(e.NewSize.Width);
    }
}
