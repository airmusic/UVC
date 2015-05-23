namespace VersionControl.UserInterface
{
    using UnityEditor;
    using UnityEngine;

    // ReSharper disable CheckNamespace
    public class VCToggleVersionControl : Editor
    // ReSharper restore CheckNamespace
    {
        
        [MenuItem("Window/UVC/Toggle Version Control %h", false, 4)]
        static void ToggleVersionControl()
        {
            VCSettings.VCEnabled = !VCSettings.VCEnabled;
            Debug.Log(string.Format("Toggle Version Control {0}",VCSettings.VCEnabled?"On":"Off"));
        }
    }
}