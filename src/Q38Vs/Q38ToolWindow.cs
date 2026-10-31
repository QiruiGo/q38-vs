using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace Q38Vs
{
    [Guid(PackageGuids.ToolWindowString)]
    public sealed class Q38ToolWindow : ToolWindowPane
    {
        public Q38ToolWindow() : base(null)
        {
            Caption = "Qwenthin (q38)";
            Content = new Q38WebViewHost();
        }
    }
}
