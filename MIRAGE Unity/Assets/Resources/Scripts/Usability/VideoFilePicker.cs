using System;
using System.Runtime.InteropServices;

namespace Mirage.Usability
{
    /// <summary>Windows' built-in file chooser; no third-party player plugin is needed.</summary>
    public static class VideoFilePicker
    {
        public static bool TryChoose(out string path, out string error)
        {
            path = null;
            error = null;
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            IntPtr fileBuffer = Marshal.AllocHGlobal(32768 * 2);
            try
            {
                Marshal.WriteInt16(fileBuffer, 0);
                var dialog = new OpenFileName
                {
                    structSize = Marshal.SizeOf(typeof(OpenFileName)),
                    owner = GetActiveWindow(),
                    filter = "Video recordings\0*.mp4;*.m4v;*.mov;*.avi;*.webm;*.wmv\0All files\0*.*\0\0",
                    filterIndex = 1,
                    file = fileBuffer,
                    maximumFile = 32768,
                    title = "Choose a recording for MIRAGE",
                    flags = 0x00080000 | 0x00001000 | 0x00000800 | 0x00000008
                };
                if (GetOpenFileName(ref dialog))
                {
                    path = Marshal.PtrToStringUni(fileBuffer);
                    return true;
                }
                int code = CommDlgExtendedError();
                if (code != 0) error = "The file chooser could not open. Paste a video path instead.";
            }
            finally { Marshal.FreeHGlobal(fileBuffer); }
#else
            error = "Paste a local video path in the field below, then choose Open recording.";
#endif
            return false;
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OpenFileName
        {
            public int structSize;
            public IntPtr owner, instance;
            [MarshalAs(UnmanagedType.LPWStr)] public string filter;
            public IntPtr customFilter;
            public int maximumCustomFilter, filterIndex;
            public IntPtr file;
            public int maximumFile;
            public IntPtr fileTitle;
            public int maximumFileTitle;
            [MarshalAs(UnmanagedType.LPWStr)] public string initialDirectory;
            [MarshalAs(UnmanagedType.LPWStr)] public string title;
            public int flags;
            public short fileOffset, extensionOffset;
            [MarshalAs(UnmanagedType.LPWStr)] public string defaultExtension;
            public IntPtr customData, hook, templateName, reserved;
            public int reserved2, flagsEx;
        }
        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetOpenFileNameW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetOpenFileName(ref OpenFileName dialog);
        [DllImport("comdlg32.dll")] private static extern int CommDlgExtendedError();
        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
#endif
    }
}
