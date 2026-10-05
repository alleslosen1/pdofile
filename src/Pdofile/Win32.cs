using System.Runtime.InteropServices;

namespace Pdofile;

internal static class Win32
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetSaveFileNameW(ref OPENFILENAME ofn);

    const int OFN_OVERWRITEPROMPT = 0x2, OFN_NOCHANGEDIR = 0x8, OFN_PATHMUSTEXIST = 0x800, OFN_FILEMUSTEXIST = 0x1000, OFN_EXPLORER = 0x80000;

    /// <summary>Shows the standard Open/Save dialog for PDF files. Returns null if cancelled.</summary>
    public static string? PickPdf(IntPtr owner, bool save, string? suggestedPath, string title)
    {
        const int max = 4096;
        IntPtr buf = Marshal.AllocHGlobal(max * 2);
        IntPtr filter = Marshal.StringToHGlobalUni("PDF files (*.pdf)\0*.pdf\0\0");
        try
        {
            string init = save && suggestedPath != null ? Path.GetFileName(suggestedPath) : "";
            var chars = (init + "\0").ToCharArray();
            Marshal.Copy(chars, 0, buf, chars.Length);
            var ofn = new OPENFILENAME
            {
                lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                hwndOwner = owner,
                lpstrFilter = filter,
                nFilterIndex = 1,
                lpstrFile = buf,
                nMaxFile = max,
                lpstrTitle = title,
                lpstrInitialDir = suggestedPath != null ? Path.GetDirectoryName(suggestedPath) : null,
                lpstrDefExt = "pdf",
                Flags = OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_PATHMUSTEXIST | (save ? OFN_OVERWRITEPROMPT : OFN_FILEMUSTEXIST),
            };
            bool ok = save ? GetSaveFileNameW(ref ofn) : GetOpenFileNameW(ref ofn);
            return ok ? Marshal.PtrToStringUni(buf) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
            Marshal.FreeHGlobal(filter);
        }
    }

    [DllImport("user32.dll")]
    static extern bool SetWindowFeedbackSetting(IntPtr hwnd, int feedback, int flags, int size, ref int config);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool SetProp(IntPtr hwnd, string name, IntPtr data);

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);

    /// <summary>
    /// Turns off Windows' pen/touch feedback (tap ripples, press-and-hold ring, flicks) for the
    /// window and its children, so holding the pen still (hold-to-shape) shows nothing extra.
    /// </summary>
    public static void DisablePenFeedback(IntPtr top)
    {
        Apply(top);
        EnumChildWindows(top, (h, _) => { Apply(h); return true; }, IntPtr.Zero);
    }

    static void Apply(IntPtr h)
    {
        int off = 0;
        for (int f = 1; f <= 11; f++) SetWindowFeedbackSetting(h, f, 0, sizeof(int), ref off);
        // TABLET_DISABLE_PRESSANDHOLD | PENTAPFEEDBACK | PENBARRELFEEDBACK | TOUCHUIFORCEOFF | FLICKS
        SetProp(h, "MicrosoftTabletPenServiceProperty", (IntPtr)(0x1 | 0x8 | 0x10 | 0x200 | 0x10000));
    }
}
