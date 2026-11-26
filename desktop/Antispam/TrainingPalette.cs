using System.Globalization;
using System.Numerics;

namespace MessengerDesktop.Antispam;

internal sealed record TrainingPalette(int Target,int Hover)
{
    public static readonly TrainingPalette Neutral=FromAverage("#989898");
    public static TrainingPalette FromAverage(string? average)
    {
        var rgb=0x989898;
        if(average is {Length:7} && average[0]=='#' && int.TryParse(average.AsSpan(1),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var parsed))rgb=parsed;
        var color=new Vector3((rgb>>16)&255,(rgb>>8)&255,rgb&255)/255f;
        var peak=Math.Max(color.X,Math.Max(color.Y,color.Z));
        // Keep the average hue/chroma ratios, but lift dark video colors so targets remain readable.
        color=peak>.02f?color*(Math.Clamp(peak,.62f,.78f)/peak):new Vector3(.62f);
        static int Pack(Vector3 c)=>((int)MathF.Round(c.X*255)<<16)|((int)MathF.Round(c.Y*255)<<8)|(int)MathF.Round(c.Z*255);
        return new(Pack(color),Pack(Vector3.Lerp(color,Vector3.One,.18f)));
    }
}
