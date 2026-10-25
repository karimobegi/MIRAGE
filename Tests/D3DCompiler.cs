using System;
using System.Runtime.InteropServices;
using System.Text;

// Tests only: compile the actual HLSL without requiring a Unity installation.
public static class MirageD3DCompiler
{
    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IBlob
    {
        [PreserveSig] IntPtr GetBufferPointer();
        [PreserveSig] UIntPtr GetBufferSize();
    }

    [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern int D3DCompile(byte[] source, UIntPtr size, string name, IntPtr defines,
        IntPtr include, string entry, string target, uint flags1, uint flags2, out IBlob code, out IBlob error);

    public static string Compile(string source, string filename, string entry)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(source);
        IBlob code, error;
        // Optimization level 1 matches a normal shader compile; errors remain fatal.
        int result = D3DCompile(bytes, (UIntPtr)bytes.Length, filename, IntPtr.Zero, IntPtr.Zero,
            entry, "cs_5_0", 0, 0, out code, out error);
        string diagnostics = error == null ? "" : Marshal.PtrToStringAnsi(error.GetBufferPointer(), (int)error.GetBufferSize().ToUInt64()).TrimEnd('\0', '\r', '\n');
        if (code != null) Marshal.ReleaseComObject(code);
        if (error != null) Marshal.ReleaseComObject(error);
        if (result < 0) throw new InvalidOperationException(filename + " :: " + entry + Environment.NewLine + diagnostics);
        return diagnostics;
    }
}
