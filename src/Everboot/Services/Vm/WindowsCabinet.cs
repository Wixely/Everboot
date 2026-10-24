using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Everboot.Services.Vm;

// Uses the operating system's cabinet decoder; no decoder executable or library
// is shipped. Outputs are restricted to an exact whitelist in a private folder.
internal static class WindowsCabinet
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileInfoInCabinet
    {
        public IntPtr Name;
        public uint Size, Error;
        public ushort Date, Time, Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Target;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct CabinetInfo
    {
        public IntPtr Path, File, Disk;
        public ushort Set, Number;
    }
    private delegate uint Callback(IntPtr context, uint notification, IntPtr p1, IntPtr p2);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupIterateCabinetW(string file, uint reserved, Callback callback, IntPtr context);

    internal static void Extract(string cabinet, string output, HashSet<string> wanted)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Preparing a DOS startup disk currently requires Windows.");
        Exception? failure = null;
        Callback callback = (_, note, p1, p2) =>
        {
            try
            {
                if (note == 0x11) // FILEINCABINET
                {
                    var info = Marshal.PtrToStructure<FileInfoInCabinet>(p1);
                    var name = Marshal.PtrToStringUni(info.Name) ?? "";
                    if (!wanted.Contains(name)) return 2; // FILEOP_SKIP
                    if (info.Size is 0 or > 524288 || name != System.IO.Path.GetFileName(name))
                        throw new IOException("Unsupported startup component in cabinet.");
                    info.Target = System.IO.Path.Combine(output, name.ToUpperInvariant());
                    if (info.Target.Length >= 260) throw new IOException("Startup staging path is too long for Windows cabinet extraction.");
                    Marshal.StructureToPtr(info, p1, false);
                    return 1; // FILEOP_DOIT
                }
                if (note == 0x12) // NEEDNEWCABINET: only already-staged, single filenames.
                {
                    var info = Marshal.PtrToStructure<CabinetInfo>(p1);
                    var name = Marshal.PtrToStringUni(info.File) ?? "";
                    if (name.Length == 0 || name.IndexOfAny(['/', '\\', ':']) >= 0 || name is "." or ".." ||
                        !File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(cabinet)!, name)))
                        throw new IOException("The DOS component requires an unavailable continuation cabinet.");
                }
                if (note == 0x13) // FILEEXTRACTED: FILEPATHS.Win32Error follows two pointers.
                {
                    var error = Marshal.ReadInt32(p1, IntPtr.Size * 2);
                    if (error != 0) throw new IOException("Windows could not extract a startup component.", new Win32Exception(error));
                }
                return 0;
            }
            catch (Exception ex) { failure = ex; return note == 0x11 ? 0u : 13u; }
        };
        var ok = SetupIterateCabinetW(cabinet, 0, callback, IntPtr.Zero);
        var lastError = Marshal.GetLastWin32Error();
        GC.KeepAlive(callback);
        if (failure is not null) throw new IOException("Could not prepare DOS startup files.", failure);
        if (!ok) throw new IOException("Windows could not read the startup cabinet.", new Win32Exception(lastError));
    }
}
