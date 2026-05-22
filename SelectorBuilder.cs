using System;
using System.Diagnostics;
using System.Windows.Automation;

namespace GlassLinq.UIExplorer
{
    public static class SelectorBuilder
    {
        public static string GenerateSelector(AutomationElement element)
        {
            try
            {
                var current = element.Current;

                // Get the Process name (e.g., notepad.exe)
                string processName = "";
                try
                {
                    var process = Process.GetProcessById(current.ProcessId);
                    processName = process.ProcessName + ".exe";
                }
                catch { processName = "unknown.exe"; }

                // Build the Window level tag
                string appPart = $"<wnd app='{processName}' ";

                // Add Title if it exists
                if (!string.IsNullOrEmpty(current.Name))
                    appPart += $"title='{current.Name}' ";

                appPart += "/>";

                // Build the Control level tag
                string ctrlPart = $"<ctrl ";

                if (!string.IsNullOrEmpty(current.AutomationId))
                    ctrlPart += $"automationid='{current.AutomationId}' ";

                ctrlPart += $"role='{current.LocalizedControlType}' ";

                if (!string.IsNullOrEmpty(current.Name))
                    ctrlPart += $"name='{current.Name}' ";

                ctrlPart += "/>";

                return $"{appPart}\n{ctrlPart}";
            }
            catch (Exception ex)
            {
                return $"";
            }
        }
    }
}