using System.Collections.Generic;
using System.Drawing;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace HelloRhinoCommon.UI;

internal sealed class StackPreviewConduit : DisplayConduit
{
    private static readonly Color PreviewColor = Color.OrangeRed;
    private static readonly DisplayMaterial PreviewMaterial = new DisplayMaterial
    {
        Diffuse = Color.FromArgb(90, 255, 99, 71),
        BackDiffuse = Color.FromArgb(90, 255, 99, 71),
    };
    private readonly HelloRhinoCommon.Runtime.ModifierEngine _engine;

    public StackPreviewConduit(HelloRhinoCommon.Runtime.ModifierEngine engine)
    {
        _engine = engine;
    }

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
        foreach (var geometry in _engine.GetPreviewGeometry(RhinoDoc.ActiveDoc))
        {
            e.IncludeBoundingBox(geometry.GetBoundingBox(true));
        }
    }

    protected override void PostDrawObjects(DrawEventArgs e)
    {
        foreach (var geometry in _engine.GetPreviewGeometry(e.RhinoDoc))
        {
            DrawGeometry(e.Display, geometry);
        }
    }

    private static void DrawGeometry(DisplayPipeline display, GeometryBase geometry)
    {
        switch (geometry)
        {
            case Rhino.Geometry.Point point:
                display.DrawPoint(point.Location, PointStyle.Simple, 4, PreviewColor);
                break;
            case Curve curve:
                display.DrawCurve(curve, PreviewColor, 2);
                break;
            case Brep brep:
                display.DrawBrepShaded(brep, PreviewMaterial);
                display.DrawBrepWires(brep, PreviewColor, 1);
                break;
            case Mesh mesh:
                display.DrawMeshShaded(mesh, PreviewMaterial);
                display.DrawMeshWires(mesh, PreviewColor);
                break;
            case SubD subD:
                display.DrawSubDShaded(subD, PreviewMaterial);
                display.DrawSubDWires(subD, PreviewColor, 1);
                break;
        }
    }
}
