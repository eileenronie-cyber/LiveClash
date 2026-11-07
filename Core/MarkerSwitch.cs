using System;
using System.IO;

namespace LiveClash.Core
{
    /// <summary>
    /// The per-PC kill switch for clash markers (spec 12.2). A marker file <c>clash-markers.off</c> in the LiveClash folder of the local
    /// application data: present means OFF, absent means ON, which is the default. It is read with <c>File.Exists</c> (no Revit call, no
    /// transaction, no parsing, so nothing in it can fail in a way that keeps the switch from working); a read that fails counts as ON.
    /// While it is off every marker write refuses, the schedule is not attached to the panel, and the panel works as it did before; markers
    /// already in a model stay as they are. To switch off, create an empty file of that name; to switch on, delete it.
    /// </summary>
    public static class MarkerSwitch
    {
        public const string FileName = "clash-markers.off";

        /// <summary>The file whose presence switches the markers off on this PC. <c>LocalState.Root</c> is the real local application data, or a dev job's scratch root.</summary>
        public static string FilePath
        {
            get { return Path.Combine(LocalState.Root, "LiveClash", FileName); }
        }

        public static bool IsOn
        {
            get
            {
                try { return !File.Exists(FilePath); }
                catch { return true; }
            }
        }

        /// <summary>Switches the markers on (deletes the file) or off (creates it). Returns false when the file could not be changed.</summary>
        public static bool Set(bool on)
        {
            try
            {
                var path = FilePath;
                if (on)
                {
                    if (File.Exists(path)) File.Delete(path);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    if (!File.Exists(path)) File.WriteAllText(path, "Clash markers are off on this PC while this file exists. Delete it to switch them on.\r\n");
                }
                return IsOn == on;
            }
            catch (Exception ex)
            {
                Log.Warn("The clash markers switch could not be changed", ex);
                return false;
            }
        }
    }
}
