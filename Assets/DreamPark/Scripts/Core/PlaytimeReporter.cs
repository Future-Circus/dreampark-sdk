// Superseded by SessionReporter.cs — kept as a thin compatibility shim
// so existing code that calls PlaytimeReporter.SetAutoReportEnabled(...)
// keeps compiling. Delete this file once nothing references it.

using System;

namespace DreamPark.API
{
    [Obsolete("Use SessionReporter instead. This class will be removed in a future SDK release.")]
    public static class PlaytimeReporter
    {
        public static bool IsAutoReportEnabled => SessionReporter.IsAutoReportEnabled;

        public static void SetAutoReportEnabled(bool enabled)
            => SessionReporter.SetAutoReportEnabled(enabled);
    }
}
