using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FlashInput
{
    /// <summary>One controller slot Windows currently sees.</summary>
    public sealed record JoystickInfo(uint Id, string Name, ushort VendorId, ushort ProductId)
    {
        // DragonRise Inc. uses vendor id 0x0079 on pretty much all their chips
        public bool IsDragonRise => VendorId == 0x0079;

        public string DisplayName =>
            $"{Name}  [{VendorId:X4}:{ProductId:X4}]{(IsDragonRise ? "  (DragonRise)" : "")}";
    }

    /// <summary>One snapshot of the raw pad state. Axes are 0..65535, centre is about 32767.</summary>
    public readonly record struct JoystickState(
        uint X, uint Y, uint Z, uint R, uint U, uint V, uint Buttons, uint Pov);

    /// <summary>
    /// Thin wrapper over the old winmm joystick api. It's built into windows, has no
    /// dependencies, and handles generic HID pads (DragonRise etc.) just fine.
    /// Nothing in here holds native memory, every call is stack-only, so nothing can leak.
    /// </summary>
    internal static class WinMmJoystick
    {
        private const uint JoyReturnAll = 0x000000FF;

        public static List<JoystickInfo> Enumerate()
        {
            var found = new List<JoystickInfo>();
            uint slots = joyGetNumDevs();

            for (uint id = 0; id < slots; id++)
            {
                var caps = new JoyCaps();
                if (joyGetDevCapsW(id, ref caps, (uint)Marshal.SizeOf<JoyCaps>()) != 0)
                    continue;

                // caps can succeed for empty slots, so make sure we can actually read it
                if (!TryRead(id, out _))
                    continue;

                string name = LookupFriendlyName(caps.szRegKey, caps.szPname);
                found.Add(new JoystickInfo(id, name, caps.wMid, caps.wPid));
            }

            return found;
        }

        public static bool TryRead(uint id, out JoystickState state)
        {
            var info = new JoyInfoEx
            {
                dwSize = (uint)Marshal.SizeOf<JoyInfoEx>(),
                dwFlags = JoyReturnAll
            };

            if (joyGetPosEx(id, ref info) != 0)
            {
                state = default;
                return false; // unplugged or driver gave up
            }

            state = new JoystickState(info.dwXpos, info.dwYpos, info.dwZpos, info.dwRpos,
                                      info.dwUpos, info.dwVpos, info.dwButtons, info.dwPOV);
            return true;
        }

        // windows usually reports "Microsoft PC-joystick driver" for everything,
        // the real product name is in the registry under the pad's VID/PID key
        private static string LookupFriendlyName(string regKey, string fallback)
        {
            if (string.IsNullOrWhiteSpace(regKey)) return fallback;

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\" + regKey);

                if (key?.GetValue("OEMName") is string name && name.Length > 0)
                    return name;
            }
            catch
            {
                // registry hiccup isn't worth failing over, just use the fallback
            }

            return fallback;
        }

        // makes the windows timer tick every 1ms instead of ~15ms so polling is smooth.
        // every Begin MUST be matched with an End or the system keeps the fast timer on.
        public static void BeginHighResTimer() => timeBeginPeriod(1);
        public static void EndHighResTimer() => timeEndPeriod(1);

        // ------------------------------------------------------------
        // native declarations
        // ------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct JoyInfoEx
        {
            public uint dwSize;
            public uint dwFlags;
            public uint dwXpos;
            public uint dwYpos;
            public uint dwZpos;
            public uint dwRpos;
            public uint dwUpos;
            public uint dwVpos;
            public uint dwButtons;
            public uint dwButtonNumber;
            public uint dwPOV;
            public uint dwReserved1;
            public uint dwReserved2;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct JoyCaps
        {
            public ushort wMid;
            public ushort wPid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint wXmin;
            public uint wXmax;
            public uint wYmin;
            public uint wYmax;
            public uint wZmin;
            public uint wZmax;
            public uint wNumButtons;
            public uint wPeriodMin;
            public uint wPeriodMax;
            public uint wRmin;
            public uint wRmax;
            public uint wUmin;
            public uint wUmax;
            public uint wVmin;
            public uint wVmax;
            public uint wCaps;
            public uint wMaxAxes;
            public uint wNumAxes;
            public uint wMaxButtons;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szRegKey;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szOEMVxD;
        }

        [DllImport("winmm.dll")] private static extern uint joyGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern uint joyGetDevCapsW(uint uJoyID, ref JoyCaps pjc, uint cbjc);
        [DllImport("winmm.dll")] private static extern uint joyGetPosEx(uint uJoyID, ref JoyInfoEx pji);
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);
    }
}