using System;
using System.Runtime.InteropServices;
using Accessibility;

namespace YTPlayer.Utils
{
    internal static class AccessibilityPropertyService
    {
        private const uint ObjIdClient = 0xFFFFFFFC;
        private static readonly Guid NamePropertyId = new Guid("608D3DF8-8128-4AA7-A428-F55E49267291");
        private static readonly Guid RolePropertyId = new Guid("CB905FF2-7BD1-4C05-B3C8-E6C2413642D2");
        private static readonly object _lock = new object();
        private static IAccPropServices? _service;
        private static bool _serviceFailed;

        public static void TrySetListItemName(IntPtr hwnd, int itemIndex, string name)
        {
            TrySetListItemProperties(hwnd, itemIndex, name, role: null);
        }

        public static void TrySetListItemProperties(IntPtr hwnd, int itemIndex, string name, int? role)
        {
            if (hwnd == IntPtr.Zero || itemIndex < 0 || string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            IAccPropServices? service = GetService();
            if (service == null)
            {
                return;
            }

            try
            {
                _RemotableHandle handle = CreateHandle(hwnd);
                uint childId = checked((uint)(itemIndex + 1));
                service.SetHwndPropStr(ref handle, ObjIdClient, childId, NamePropertyId, name);
                if (role.HasValue)
                {
                    service.SetHwndProp(ref handle, ObjIdClient, childId, RolePropertyId, role.Value);
                }
            }
            catch (COMException)
            {
                _serviceFailed = true;
            }
            catch (Exception)
            {
            }
        }

        private static IAccPropServices? GetService()
        {
            lock (_lock)
            {
                if (_serviceFailed)
                {
                    return null;
                }
                if (_service == null)
                {
                    try
                    {
                        _service = new CAccPropServicesClass();
                    }
                    catch (Exception)
                    {
                        _serviceFailed = true;
                        return null;
                    }
                }
                return _service;
            }
        }

        private static _RemotableHandle CreateHandle(IntPtr hwnd)
        {
            _RemotableHandle handle = new _RemotableHandle();
            handle.fContext = 0;
            handle.u = new __MIDL_IWinTypes_0009
            {
                hInproc = unchecked((int)hwnd.ToInt64()),
                hRemote = 0
            };
            return handle;
        }
    }
}
