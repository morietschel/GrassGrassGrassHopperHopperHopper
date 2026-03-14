using Rhino.Commands;
using Rhino.UI;
using HelloRhinoCommon.UI;

namespace HelloRhinoCommon.Commands;

public sealed class GghOpenPanelCommand : Command
{
    public override string EnglishName => "GghOpenPanel";

    protected override Result RunCommand(Rhino.RhinoDoc doc, RunMode mode)
    {
        Rhino.RhinoApp.WriteLine("GGH: Opening modifier stack panel.");
        Panels.OpenPanel(typeof(ModifierStackPanel));
        return Result.Success;
    }
}
