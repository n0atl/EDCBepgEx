namespace EpgCollector;

internal static class Ui
{
    public static readonly Icon AppIcon=LoadIcon();
    static Icon LoadIcon()
    {
        using var stream=typeof(Ui).Assembly.GetManifestResourceStream("EDCBepgEx.AppIcon")!;
        using var icon=new Icon(stream);return (Icon)icon.Clone();
    }
    public static Button Button(string text,bool enabled=true)=>new()
    {
        Text=text,Enabled=enabled,AutoSize=false,Size=new(84,24),MinimumSize=new(84,24),
        Padding=Padding.Empty,Margin=new(3),Font=new("Yu Gothic UI",9),UseVisualStyleBackColor=true
    };
}
