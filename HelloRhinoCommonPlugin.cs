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
        private bool _openedPropertiesOnStartup;

        public HelloRhinoCommonPlugin()
        {
            Instance = this;
        }
        
        ///<summary>Gets the only instance of the HelloRhinoCommonPlugin plug-in.</summary>
        public static HelloRhinoCommonPlugin Instance { get; private set; } = null!;

        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        internal ModifierEngine Engine { get; private set; } = null!;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            Engine = new ModifierEngine();
            RhinoApp.Initialized += OnRhinoInitialized;
            RhinoApp.Idle += OnRhinoIdle;
            return LoadReturnCode.Success;
        }

        protected override void OnShutdown()
        {
            RhinoApp.Initialized -= OnRhinoInitialized;
            RhinoApp.Idle -= OnRhinoIdle;
            Engine.Dispose();
            base.OnShutdown();
        }

        protected override void ObjectPropertiesPages(ObjectPropertiesPageCollection collection)
        {
            collection.Add(new ModifierObjectPropertiesPage());
        }

        private void OnRhinoInitialized(object? sender, EventArgs e)
        {
            RhinoApp.Initialized -= OnRhinoInitialized;
            RhinoApp.InvokeOnUiThread(OpenPropertiesPanelOnStartup);
        }

        private void OnRhinoIdle(object? sender, EventArgs e)
        {
            if (_openedPropertiesOnStartup)
            {
                RhinoApp.Idle -= OnRhinoIdle;
                return;
            }

            RhinoApp.Idle -= OnRhinoIdle;
            OpenPropertiesPanelOnStartup();
        }

        private void OpenPropertiesPanelOnStartup()
        {
            if (_openedPropertiesOnStartup)
            {
                return;
            }

            _openedPropertiesOnStartup = true;

            if (!Panels.IsPanelVisible(PanelIds.ObjectProperties))
            {
                Panels.OpenPanel(PanelIds.ObjectProperties);
            }
        }
    }
}
