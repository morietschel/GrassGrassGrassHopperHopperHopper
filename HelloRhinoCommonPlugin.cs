using System;
using HelloRhinoCommon.Runtime;
using HelloRhinoCommon.UI;
using Rhino;
using Rhino.PlugIns;
using Rhino.UI;

namespace HelloRhinoCommon
{
    ///<summary>
    /// <para>Every RhinoCommon .rhp assembly must have one and only one PlugIn-derived
    /// class. DO NOT create instances of this class yourself. It is the
    /// responsibility of Rhino to create an instance of this class.</para>
    /// <para>To complete plug-in information, please also see all PlugInDescription
    /// attributes in AssemblyInfo.cs (you might need to click "Project" ->
    /// "Show All Files" to see it in the "Solution Explorer" window).</para>
    ///</summary>
    public class HelloRhinoCommonPlugin : PlugIn
    {
        public HelloRhinoCommonPlugin()
        {
            Instance = this;
        }
        
        ///<summary>Gets the only instance of the HelloRhinoCommonPlugin plug-in.</summary>
        public static HelloRhinoCommonPlugin Instance { get; private set; } = null!;

        internal ModifierEngine Engine { get; private set; } = null!;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            Engine = new ModifierEngine();
            return LoadReturnCode.Success;
        }

        protected override void OnShutdown()
        {
            Engine.Dispose();
            base.OnShutdown();
        }

        protected override void ObjectPropertiesPages(ObjectPropertiesPageCollection collection)
        {
            collection.Add(new ModifierObjectPropertiesPage());
        }
    }
}
