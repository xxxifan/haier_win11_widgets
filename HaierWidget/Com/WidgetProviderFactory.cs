// 参考微软官方样例 WindowsAppSDK-Samples/Samples/Widgets/cs-console-packaged/WidgetHelper
using System.Runtime.InteropServices;
using HaierWidget.Util;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

namespace HaierWidget.Com;

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("00000001-0000-0000-C000-000000000046")]
internal interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);

    [PreserveSig]
    int LockServer(bool fLock);
}

internal static class ClassObject
{
    private const uint ClsctxLocalServer = 0x4;
    private const uint RegclsMultipleUse = 0x1;

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
        uint dwClsContext,
        uint flags,
        out uint lpdwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint dwRegister);

    public static uint Register(Guid clsid, object factory)
    {
        int hr = CoRegisterClassObject(clsid, factory, ClsctxLocalServer, RegclsMultipleUse, out uint cookie);
        if (hr != 0)
        {
            Marshal.ThrowExceptionForHR(hr);
        }
        return cookie;
    }

    public static void Revoke(uint cookie) => CoRevokeClassObject(cookie);
}

/// <summary>为 WidgetProvider 提供 IClassFactory，小组件面板通过它 CoCreateInstance。</summary>
[ComVisible(true)]
internal sealed class WidgetProviderFactory<T> : IClassFactory where T : class, IWidgetProvider, new()
{
    private static readonly Guid IUnknownGuid = new("00000000-0000-0000-C000-000000000046");
    private const int ClassENoAggregation = unchecked((int)0x80040110);
    private const int ENoInterface = unchecked((int)0x80004002);

    public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;
        if (pUnkOuter != IntPtr.Zero)
        {
            return ClassENoAggregation;
        }

        try
        {
            var instance = new T();
            if (riid == typeof(IWidgetProvider).GUID || riid == IUnknownGuid)
            {
                ppvObject = MarshalInspectable<IWidgetProvider>.FromManaged(instance);
                return 0;
            }
            // 其它接口（例如 IWidgetProvider2）通过 QueryInterface 获取
            IntPtr unknown = MarshalInspectable<IWidgetProvider>.FromManaged(instance);
            try
            {
                int hr = Marshal.QueryInterface(unknown, ref riid, out ppvObject);
                if (hr != 0)
                {
                    Log.Warn($"CreateInstance: 不支持的接口 {riid} hr=0x{hr:X8}");
                    return ENoInterface;
                }
                return 0;
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }
        catch (Exception ex)
        {
            Log.Error("CreateInstance 失败", ex);
            return ex.HResult != 0 ? ex.HResult : ENoInterface;
        }
    }

    public int LockServer(bool fLock) => 0;
}
