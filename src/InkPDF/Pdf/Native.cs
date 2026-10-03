using System.Runtime.InteropServices;

namespace InkPDF.Pdf;

[StructLayout(LayoutKind.Sequential)]
public struct FS_POINTF { public float X, Y; }

[StructLayout(LayoutKind.Sequential)]
public struct FS_RECTF { public float Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
public struct FS_SIZEF { public float Width, Height; }

/// <summary>PDFium P/Invoke surface. Note: C "unsigned long" is 32-bit on Windows.</summary>
internal static unsafe class Native
{
    const string Lib = "pdfium";

    public const int FPDF_ANNOT = 0x01;
    public const int FPDFBitmap_BGRA = 4;
    public const int FPDF_ANNOT_INK = 15;
    public const int FPDF_PAGEOBJ_PATH = 2;
    public const uint FPDF_NO_INCREMENTAL = 2;
    public const int FPDF_ANNOT_FLAG_PRINT = 4;

    [DllImport(Lib)] public static extern void FPDF_InitLibrary();
    [DllImport(Lib)] public static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, nuint size, [MarshalAs(UnmanagedType.LPStr)] string? password);
    [DllImport(Lib)] public static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport(Lib)] public static extern uint FPDF_GetLastError();
    [DllImport(Lib)] public static extern int FPDF_GetPageCount(IntPtr doc);
    [DllImport(Lib)] public static extern int FPDF_GetPageSizeByIndexF(IntPtr doc, int index, out FS_SIZEF size);
    [DllImport(Lib)] public static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [DllImport(Lib)] public static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Lib)] public static extern int FPDF_SaveAsCopy(IntPtr doc, void* fileWrite, uint flags);

    [DllImport(Lib)] public static extern IntPtr FPDFBitmap_CreateEx(int width, int height, int format, IntPtr firstScan, int stride);
    [DllImport(Lib)] public static extern int FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [DllImport(Lib)] public static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport(Lib)] public static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int flags);
    [DllImport(Lib)] public static extern int FPDF_DeviceToPage(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, int deviceX, int deviceY, out double pageX, out double pageY);

    [DllImport(Lib)] public static extern int FPDFPage_GetAnnotCount(IntPtr page);
    [DllImport(Lib)] public static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
    [DllImport(Lib)] public static extern void FPDFPage_CloseAnnot(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFPage_RemoveAnnot(IntPtr page, int index);
    [DllImport(Lib)] public static extern IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [DllImport(Lib)] public static extern int FPDFPage_Flatten(IntPtr page, int flag);

    [DllImport(Lib)] public static extern int FPDFAnnot_GetSubtype(IntPtr annot);
    [DllImport(Lib)] public static extern uint FPDFAnnot_GetInkListCount(IntPtr annot);
    [DllImport(Lib)] public static extern uint FPDFAnnot_GetInkListPath(IntPtr annot, uint pathIndex, FS_POINTF* buffer, uint length);
    [DllImport(Lib)] public static extern int FPDFAnnot_AddInkStroke(IntPtr annot, FS_POINTF* points, nuint count);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetColor(IntPtr annot, int type, out uint r, out uint g, out uint b, out uint a);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetColor(IntPtr annot, int type, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetBorder(IntPtr annot, out float hRadius, out float vRadius, out float width);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetBorder(IntPtr annot, float hRadius, float vRadius, float width);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetRect(IntPtr annot, ref FS_RECTF rect);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetFlags(IntPtr annot, int flags);
    [DllImport(Lib)] public static extern int FPDFAnnot_HasKey(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key);
    [DllImport(Lib)] public static extern uint FPDFAnnot_GetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, ushort* buffer, uint length);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPWStr)] string value);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetNumberValue(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, out float value);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetAP(IntPtr annot, int mode, [MarshalAs(UnmanagedType.LPWStr)] string? value);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetObjectCount(IntPtr annot);
    [DllImport(Lib)] public static extern IntPtr FPDFAnnot_GetObject(IntPtr annot, int index);
    [DllImport(Lib)] public static extern int FPDFAnnot_AppendObject(IntPtr annot, IntPtr obj);

    [DllImport(Lib)] public static extern int FPDFPageObj_GetType(IntPtr obj);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetStrokeColor(IntPtr obj, out uint r, out uint g, out uint b, out uint a);
    [DllImport(Lib)] public static extern int FPDFPageObj_GetStrokeWidth(IntPtr obj, out float width);
    [DllImport(Lib)] public static extern IntPtr FPDFPageObj_CreateNewPath(float x, float y);
    [DllImport(Lib)] public static extern int FPDFPath_LineTo(IntPtr path, float x, float y);
    [DllImport(Lib)] public static extern int FPDFPath_BezierTo(IntPtr path, float x1, float y1, float x2, float y2, float x3, float y3);
    [DllImport(Lib)] public static extern int FPDFPath_SetDrawMode(IntPtr path, int fillMode, int stroke);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetStrokeColor(IntPtr obj, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetStrokeWidth(IntPtr obj, float width);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetLineCap(IntPtr obj, int cap);
    [DllImport(Lib)] public static extern int FPDFPageObj_SetLineJoin(IntPtr obj, int join);
    [DllImport(Lib)] public static extern void FPDFPageObj_SetBlendMode(IntPtr obj, [MarshalAs(UnmanagedType.LPStr)] string mode);
    [DllImport(Lib)] public static extern void FPDFPageObj_Destroy(IntPtr obj);
}
