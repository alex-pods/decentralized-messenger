using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace MessengerDesktop.Antispam;

public sealed partial class AntispamWindow
{
    void ShowResult(AimState state, bool passed)
    {
        _passed=passed; _ended=true; ShowingResult=true;
        Failure=passed?null:"6000 очков не набрано. Попробуйте снова.";
        Cleanup(); Cursor=Avalonia.Input.Cursor.Default;
        _sound.Play("finish");
        TextBlock Label(string text,double size,string color)=>new() { Text=text,FontSize=size,Foreground=Brush.Parse(color),HorizontalAlignment=HorizontalAlignment.Center };
        var stats=new Grid { ColumnDefinitions=new ColumnDefinitions("*,*,*"),Margin=new Thickness(0,24,0,28) };
        var values=new[]{(state.Hits.ToString(),"Попадания"),(state.Misses.ToString(),"Промахи"),(state.Accuracy.ToString("0.0")+"%","Точность")};
        for(var i=0;i<values.Length;i++)
        {
            var column=new StackPanel { Spacing=6,Children={Label(values[i].Item1,26,"#D3E5EE"),Label(values[i].Item2,13,"#92A9B8")} };
            Grid.SetColumn(column,i);stats.Children.Add(column);
        }
        var button=new Button { Content="Вернуться в чат",Background=Brush.Parse("#94D6EE"),Foreground=Brush.Parse("#111E29"),
            CornerRadius=new CornerRadius(4),Padding=new Thickness(24,12),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Center,MinHeight=44 };
        button.Click+=(_,_)=>DismissResult();
        Content=new Grid { Background=Brush.Parse("#111E29"),Children={new Border {
            MaxWidth=460,Margin=new Thickness(24),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,
            Child=new StackPanel { Spacing=12,Children={Label("ТОЧНОСТЬ · 30 СЕКУНД",12,"#92A9B8"),
                Label(passed?"Проверка пройдена":"Попробуйте ещё раз",26,"#D3E5EE"),
                Label(state.Score.ToString("N0"),64,"#94D6EE"),Label("Нужно 6 000 очков",14,"#92A9B8"),stats,button} }
        }}};
    }
}
