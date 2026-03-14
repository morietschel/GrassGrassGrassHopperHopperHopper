using Rhino.UI;

namespace HelloRhinoCommon.UI;

public sealed class ModifierObjectPropertiesPage : ObjectPropertiesPage
{
    private readonly ModifierStackPanel _panel = new();

    public override string EnglishPageTitle => "GGH Stack";

    public override object PageControl => _panel;

    public override bool ShouldDisplay(ObjectPropertiesPageEventArgs e)
    {
        return e.ObjectCount == 1;
    }

    public override void UpdatePage(ObjectPropertiesPageEventArgs e)
    {
        _panel.RefreshNow();
    }
}
