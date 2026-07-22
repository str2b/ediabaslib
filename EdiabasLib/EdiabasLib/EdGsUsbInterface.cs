using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace EdiabasLib
{
    public class EdGsUsbInterface
    {
        // SetupAPI P/Invokes to find device path
        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

        private static readonly Guid GsUsbGuidDefault = new Guid("c15b4308-04d3-11e6-b3ea-6057189e6443"); // Default gs_usb WinUSB interface GUID

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SP_DEVICE_INTERFACE_DETAIL_DATA
        {
            public int cbSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string DevicePath;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid ClassGuid, string Enumerator, IntPtr hwndParent, uint Flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr DeviceInfoSet, IntPtr DeviceInfoData, ref Guid InterfaceClassGuid, uint MemberIndex, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr DeviceInfoSet, ref SP_DEVICE_INTERFACE_DATA DeviceInterfaceData, ref SP_DEVICE_INTERFACE_DETAIL_DATA DeviceInterfaceDetailData, uint DeviceInterfaceDetailDataSize, out uint RequiredSize, ref SP_DEVINFO_DATA DeviceInfoData);

        [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr DeviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        // WinUSB P/Invokes
        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Initialize(SafeFileHandle DeviceHandle, out IntPtr InterfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Free(IntPtr InterfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_FlushPipe(IntPtr InterfaceHandle, byte PipeID);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_SetPipePolicy(IntPtr InterfaceHandle, byte PipeID, uint PolicyType, uint ValueLength, ref uint Value);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ControlTransfer(IntPtr InterfaceHandle, ulong SetupPacket, IntPtr Buffer, uint BufferLength, out uint LengthTransferred, IntPtr Overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_WritePipe(IntPtr InterfaceHandle, byte PipeID, byte[] Buffer, uint BufferLength, out uint LengthTransferred, IntPtr Overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ReadPipe(IntPtr InterfaceHandle, byte PipeID, byte[] Buffer, uint BufferLength, out uint LengthTransferred, IntPtr Overlapped);

        private static bool ControlTransfer(byte requestType, byte request, ushort value, ushort index, IntPtr buffer, ushort length)
        {
            ulong setupPacket = (ulong)requestType | 
                                ((ulong)request << 8) | 
                                ((ulong)value << 16) | 
                                ((ulong)index << 32) | 
                                ((ulong)length << 48);

            uint transferred = 0;
            bool res = WinUsb_ControlTransfer(_winusbHandle, setupPacket, buffer, length, out transferred, IntPtr.Zero);
            if (!res)
            {
                LogFormat("ControlTransfer failed: ReqType=0x{0:X2}, Req={1}, Err={2}", requestType, request, Marshal.GetLastWin32Error());
            }
            return res;
        }

        // gs_usb protocol structs & enums
        private const byte GS_USB_BREQ_HOST_FORMAT = 0;
        private const byte GS_USB_BREQ_BITTIMING = 1;
        private const byte GS_USB_BREQ_MODE = 2;
        private const byte GS_USB_BREQ_BERR = 3;
        private const byte GS_USB_BREQ_BT_CONST = 4;
        private const byte GS_USB_BREQ_DEVICE_CONFIG = 5;
        private const byte GS_USB_BREQ_TIMESTAMP = 6;
        private const byte GS_USB_BREQ_IDENTIFY = 7;

        private const uint GS_CAN_MODE_START = 1;
        private const uint GS_CAN_MODE_RESET = 0;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct gs_device_config
        {
            public byte reserved1;
            public byte reserved2;
            public byte reserved3;
            public byte icount;
            public uint sw_version;
            public uint hw_version;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct gs_device_bittiming
        {
            public uint prop_seg;
            public uint phase_seg1;
            public uint phase_seg2;
            public uint sjw;
            public uint brp;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct gs_device_mode
        {
            public uint mode;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct gs_device_bt_const
        {
            public uint feature;
            public uint fclk_can;
            public uint tseg1_min;
            public uint tseg1_max;
            public uint tseg2_min;
            public uint tseg2_max;
            public uint sjw_max;
            public uint brp_min;
            public uint brp_max;
            public uint brp_inc;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct gs_host_frame
        {
            public uint echo_id;
            public uint can_id;
            public byte can_dlc;
            public byte channel;
            public byte flags;
            public byte reserved;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] data;
        }

        private const uint GS_CAN_RX_ECHO_ID = 0xFFFFFFFF;

        private static byte _lastTesterAddr = 0;
        private static byte _lastEcuAddr = 0;

        private static uint GetTxCanId(byte testerAddr = 0)
        {
            if (_canTxId != -1)
            {
                return (uint)_canTxId;
            }
            if (testerAddr != 0)
            {
                _lastTesterAddr = testerAddr;
            }
            return 0x600U | _lastTesterAddr;
        }

        private static uint GetRxCanId()
        {
            if (_canRxId != -1)
            {
                return (uint)_canRxId;
            }
            return 0x600U | _lastEcuAddr;
        }

        // Instance state
        public static EdiabasNet Ediabas { get; set; }

        private static void Log(string message)
        {
            Ediabas?.LogString(EdiabasNet.EdLogLevel.Ifh, message);
        }

        private static void LogFormat(string format, params object[] args)
        {
            string message = string.Format(format, args);
            Ediabas?.LogString(EdiabasNet.EdLogLevel.Ifh, message);
        }
        private static SafeFileHandle _deviceHandle;
        private static IntPtr _winusbHandle = IntPtr.Zero;
        private static int _channelIndex = 0;
        private static int _activeBaudRate = 500000;
        private static int _canTxId = -1;
        private static int _canRxId = -1;

        private static Thread _readThread;
        private static volatile bool _terminateReadThread;
        private static readonly Queue<byte[]> _receivedUdsQueue = new Queue<byte[]>();
        private static readonly object _receivedQueueLock = new object();
        private static readonly AutoResetEvent _receiveDataEvent = new AutoResetEvent(false);
        private static readonly AutoResetEvent _fcEvent = new AutoResetEvent(false);
        private static byte _fcSTmin = 0;

        // ISO-TP RX State Machine variables
        private static byte[] _isoTpRxBuffer;
        private static int _isoTpRxLength = 0;
        private static int _isoTpRxExpectedSeq = 1;
        private static readonly object _isoTpRxLock = new object();



        // Safe cleanup
        private static void Cleanup()
        {
            _terminateReadThread = true;
            if (_readThread != null)
            {
                _readThread.Join(1000);
                _readThread = null;
            }

            if (_winusbHandle != IntPtr.Zero)
            {
                // Reset CAN channel to stop it
                SetDeviceMode(GS_CAN_MODE_RESET);
                WinUsb_Free(_winusbHandle);
                _winusbHandle = IntPtr.Zero;
            }

            if (_deviceHandle != null && !_deviceHandle.IsInvalid)
            {
                _deviceHandle.Dispose();
                _deviceHandle = null;
            }

            lock (_receivedQueueLock)
            {
                _receivedUdsQueue.Clear();
            }
        }

        // SetupAPI search by GUID
        private static string FindDevicePath(int targetIndex, Guid searchGuid)
        {
            uint flags = 0x12; // DIGCF_DEVICEINTERFACE | DIGCF_PRESENT
            IntPtr infoSet = SetupDiGetClassDevs(ref searchGuid, null, IntPtr.Zero, flags);
            if (infoSet == (IntPtr)(-1))
            {
                int err = Marshal.GetLastWin32Error();
                LogFormat("SetupDiGetClassDevs failed with error: {0}", err);
                return null;
            }

            try
            {
                SP_DEVICE_INTERFACE_DATA ifaceData = new SP_DEVICE_INTERFACE_DATA();
                ifaceData.cbSize = Marshal.SizeOf(ifaceData);

                uint memberIndex = 0;
                int foundCount = 0;

                while (SetupDiEnumDeviceInterfaces(infoSet, IntPtr.Zero, ref searchGuid, memberIndex, ref ifaceData))
                {
                    if (foundCount == targetIndex)
                    {
                        SP_DEVINFO_DATA devInfoData = new SP_DEVINFO_DATA();
                        devInfoData.cbSize = Marshal.SizeOf(devInfoData);

                        SP_DEVICE_INTERFACE_DETAIL_DATA detailData = new SP_DEVICE_INTERFACE_DETAIL_DATA();
                        detailData.cbSize = (IntPtr.Size == 8) ? 8 : 6;

                        uint detailSize = (uint)Marshal.SizeOf(detailData);
                        uint requiredSize = 0;

                        if (SetupDiGetDeviceInterfaceDetail(infoSet, ref ifaceData, ref detailData, detailSize, out requiredSize, ref devInfoData))
                        {
                            return detailData.DevicePath;
                        }
                    }
                    foundCount++;
                    memberIndex++;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(infoSet);
            }

            return null;
        }

        // Setup baud rate timing for STM32 48 MHz CAN clock
        private static bool SetBitTiming(int baudRate)
        {
            gs_device_bittiming timing = new gs_device_bittiming();
            if (baudRate == 500000)
            {
                timing.prop_seg = 0;
                timing.phase_seg1 = 11;
                timing.phase_seg2 = 4;
                timing.sjw = 3;
                timing.brp = 6;
            }
            else if (baudRate == 100000)
            {
                timing.prop_seg = 0;
                timing.phase_seg1 = 11;
                timing.phase_seg2 = 4;
                timing.sjw = 3;
                timing.brp = 30;
            }
            else
            {
                LogFormat("Unsupported gs_usb baud rate: {0}", baudRate);
                return false;
            }

            int length = Marshal.SizeOf(timing);
            IntPtr ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(timing, ptr, false);
                bool res = ControlTransfer(0x41, GS_USB_BREQ_BITTIMING, 0, (ushort)_channelIndex, ptr, (ushort)length);
                if (!res)
                {
                    LogFormat("SetBitTiming control transfer failed: {0}", Marshal.GetLastWin32Error());
                }
                return res;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        private static bool SetDeviceMode(uint mode)
        {
            gs_device_mode deviceMode = new gs_device_mode
            {
                mode = mode,
                flags = 0 // normal mode
            };

            int length = Marshal.SizeOf(deviceMode);
            IntPtr ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(deviceMode, ptr, false);
                bool res = ControlTransfer(0x41, GS_USB_BREQ_MODE, 0, (ushort)_channelIndex, ptr, (ushort)length);
                if (!res)
                {
                    LogFormat("SetDeviceMode control transfer failed for mode {0}: {1}", mode, Marshal.GetLastWin32Error());
                }
                return res;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }

        public static bool InterfaceSetDtr(bool dtr)
        {
            return true;
        }

        public static bool InterfaceSetRts(bool rts)
        {
            return true;
        }

        public static bool InterfaceGetDsr(out bool dsr)
        {
            dsr = true;
            return true;
        }

        public static bool InterfaceSetBreak(bool enable)
        {
            return true;
        }

        // Connect delegate
        public static bool InterfaceConnect(string port, object parameter)
        {
            Cleanup();

            _channelIndex = 0;
            string portUpper = port.ToUpperInvariant();
            if (portUpper.StartsWith("GS_USB:"))
            {
                string channelStr = portUpper.Substring(7);
                if (int.TryParse(channelStr, out int chIdx))
                {
                    _channelIndex = chIdx;
                }
            }

            Guid searchGuid = GsUsbGuidDefault;

            string configGuidStr = Ediabas?.GetConfigProperty("GsUsbGuid");
            if (!string.IsNullOrEmpty(configGuidStr) && Guid.TryParse(configGuidStr, out Guid parsedGuid))
            {
                searchGuid = parsedGuid;
            }

            LogFormat("Connecting to gs_usb index: {0} using GUID: {1}", _channelIndex, searchGuid);

            string devicePath = FindDevicePath(_channelIndex, searchGuid);
            if (devicePath == null)
            {
                Log("gs_usb device interface not found.");
                return false;
            }

            _deviceHandle = CreateFile(
                devicePath,
                GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero,
                OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED,
                IntPtr.Zero
            );

            if (_deviceHandle.IsInvalid)
            {
                LogFormat("Failed to open file handle: {0}", Marshal.GetLastWin32Error());
                return false;
            }

            if (!WinUsb_Initialize(_deviceHandle, out _winusbHandle))
            {
                LogFormat("WinUsb_Initialize failed: {0}", Marshal.GetLastWin32Error());
                _deviceHandle.Dispose();
                _deviceHandle = null;
                return false;
            }

            // Set a pipe timeout on the Bulk IN pipe to prevent concurrent blocking read locks
            uint timeoutMs = 10;
            if (!WinUsb_SetPipePolicy(_winusbHandle, 0x81, 0x03, 4, ref timeoutMs))
            {
                LogFormat("Failed to set read pipe timeout policy: {0}", Marshal.GetLastWin32Error());
            }

            // Host Format Handshake (0x0000beef signature to detect endianness)
            int formatLen = 4;
            IntPtr formatPtr = Marshal.AllocHGlobal(formatLen);
            try
            {
                byte[] formatBuffer = new byte[] { 0xEF, 0xBE, 0x00, 0x00 };
                Marshal.Copy(formatBuffer, 0, formatPtr, formatLen);
                if (!ControlTransfer(0x41, GS_USB_BREQ_HOST_FORMAT, 1, 0, formatPtr, (ushort)formatLen))
                {
                    LogFormat("Failed to send host format handshake: {0}", Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(formatPtr);
            }

            // Read config
            int configLen = Marshal.SizeOf(typeof(gs_device_config));
            IntPtr configPtr = Marshal.AllocHGlobal(configLen);
            try
            {
                if (ControlTransfer(0xC1, GS_USB_BREQ_DEVICE_CONFIG, 0, 0, configPtr, (ushort)configLen))
                {
                    gs_device_config config = (gs_device_config)Marshal.PtrToStructure(configPtr, typeof(gs_device_config));
                    LogFormat("gs_usb config read. SW version: {0}, HW version: {1}", config.sw_version, config.hw_version);
                }
                else
                {
                    LogFormat("Failed to read gs_usb device config: {0}", Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(configPtr);
            }

            // Read bit timing constants
            int btConstLen = Marshal.SizeOf(typeof(gs_device_bt_const));
            IntPtr btConstPtr = Marshal.AllocHGlobal(btConstLen);
            try
            {
                if (ControlTransfer(0xC1, GS_USB_BREQ_BT_CONST, 0, 0, btConstPtr, (ushort)btConstLen))
                {
                    gs_device_bt_const btConst = (gs_device_bt_const)Marshal.PtrToStructure(btConstPtr, typeof(gs_device_bt_const));
                    LogFormat("gs_usb CAN clock: {0} Hz. tseg1: {1}-{2}, tseg2: {3}-{4}, sjw_max: {5}, brp: {6}-{7}",
                        btConst.fclk_can, btConst.tseg1_min, btConst.tseg1_max, btConst.tseg2_min, btConst.tseg2_max, btConst.sjw_max, btConst.brp_min, btConst.brp_max);
                }
                else
                {
                    LogFormat("Failed to read gs_usb bit timing constants: {0}", Marshal.GetLastWin32Error());
                }
            }
            finally
            {
                Marshal.FreeHGlobal(btConstPtr);
            }

            if (!SetBitTiming(_activeBaudRate))
            {
                Cleanup();
                return false;
            }

            if (!SetDeviceMode(GS_CAN_MODE_START))
            {
                Cleanup();
                return false;
            }

            // Start background receiver thread
            _terminateReadThread = false;
            _readThread = new Thread(ReadThreadFunc)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            _readThread.Start();

            Log("Connected to gs_usb successfully.");
            return true;
        }

        public static bool InterfaceDisconnect()
        {
            Ediabas?.LogString(EdiabasNet.EdLogLevel.Ifh, "Disconnecting gs_usb.");
            Cleanup();
            return true;
        }

        public static EdInterfaceObd.InterfaceErrorResult InterfaceSetConfig(EdInterfaceObd.Protocol protocol, int baudRate, int dataBits, EdInterfaceObd.SerialParity parity, bool allowBitBang)
        {
            if (protocol != EdInterfaceObd.Protocol.IsoTp && protocol != EdInterfaceObd.Protocol.Uart)
            {
                Ediabas?.LogFormat(EdiabasNet.EdLogLevel.Ifh, "gs_usb interface unsupported protocol: {0}", protocol);
                return EdInterfaceObd.InterfaceErrorResult.DeviceTypeError;
            }

            if (protocol == EdInterfaceObd.Protocol.IsoTp && baudRate != _activeBaudRate && baudRate > 0)
            {
                if (baudRate == 500000 || baudRate == 100000)
                {
                    _activeBaudRate = baudRate;
                    if (_winusbHandle != IntPtr.Zero)
                    {
                        SetDeviceMode(GS_CAN_MODE_RESET);
                        SetBitTiming(_activeBaudRate);
                        SetDeviceMode(GS_CAN_MODE_START);
                    }
                }
            }

            return EdInterfaceObd.InterfaceErrorResult.NoError;
        }

        public static bool InterfaceSetCanIds(int canTxId, int canRxId, EdInterfaceObd.CanFlags canFlags)
        {
            _canTxId = canTxId;
            _canRxId = canRxId;
            return true;
        }

        private static bool WriteCanFrame(uint canId, byte[] data, byte dlc)
        {
            if (_winusbHandle == IntPtr.Zero) return false;

            gs_host_frame frame = new gs_host_frame
            {
                echo_id = 0, // we do not track echo_id here
                can_id = canId,
                can_dlc = dlc,
                channel = (byte)_channelIndex,
                flags = 0,
                reserved = 0,
                data = new byte[8]
            };
            Array.Copy(data, frame.data, Math.Min((int)dlc, 8));

            byte[] buffer = new byte[20]; // sizeof(gs_host_frame)
            buffer[0] = (byte)frame.echo_id;
            buffer[1] = (byte)(frame.echo_id >> 8);
            buffer[2] = (byte)(frame.echo_id >> 16);
            buffer[3] = (byte)(frame.echo_id >> 24);

            buffer[4] = (byte)frame.can_id;
            buffer[5] = (byte)(frame.can_id >> 8);
            buffer[6] = (byte)(frame.can_id >> 16);
            buffer[7] = (byte)(frame.can_id >> 24);

            buffer[8] = frame.can_dlc;
            buffer[9] = frame.channel;
            buffer[10] = frame.flags;
            buffer[11] = frame.reserved;

            Array.Copy(frame.data, 0, buffer, 12, 8);

            bool res = WinUsb_WritePipe(_winusbHandle, 0x02, buffer, (uint)buffer.Length, out _, IntPtr.Zero);
            if (!res)
            {
                LogFormat("WinUsb_WritePipe failed: {0}", Marshal.GetLastWin32Error());
            }
            return res;
        }

        // Interface SendData
        public static bool InterfaceSendData(byte[] sendData, int length, bool setDtr, double dtrTimeCorr)
        {
            if (_winusbHandle == IntPtr.Zero) return false;

            if (length < 4) return false;
            byte ecuAddr = sendData[1]; // Extract target ECU address
            byte testerAddr = sendData[2]; // Extract tester address dynamically from caller
            _lastEcuAddr = ecuAddr; // Capture ECU address dynamically for RX CAN filtering
            uint txCanId = GetTxCanId(testerAddr);

            int udsPayloadStart = 3;
            int udsPayloadLen = length - 4;

            if ((sendData[0] & 0x3F) == 0x00)
            {
                if (sendData[3] == 0)
                {
                    udsPayloadStart = 6;
                    udsPayloadLen = length - 7;
                }
                else
                {
                    udsPayloadStart = 4;
                    udsPayloadLen = length - 5;
                }
            }

            if (udsPayloadLen < 0) return false;
            byte[] udsPayload = new byte[udsPayloadLen];
            Array.Copy(sendData, udsPayloadStart, udsPayload, 0, udsPayloadLen);

            byte[] echo = new byte[length];
            Array.Copy(sendData, echo, length);
            lock (_receivedQueueLock)
            {
                _receivedUdsQueue.Enqueue(echo);
            }
            _receiveDataEvent.Set();

            if (udsPayloadLen <= 6)
            {
                // Single Frame (SF)
                byte[] canData = new byte[8];
                canData[0] = ecuAddr;
                canData[1] = (byte)udsPayloadLen;
                Array.Copy(udsPayload, 0, canData, 2, udsPayloadLen);
                return WriteCanFrame(txCanId, canData, 8);
            }
            else
            {
                // First Frame (FF)
                byte[] canData = new byte[8];
                canData[0] = ecuAddr;
                canData[1] = (byte)(0x10 | ((udsPayloadLen >> 8) & 0x0F));
                canData[2] = (byte)(udsPayloadLen & 0xFF);
                Array.Copy(udsPayload, 0, canData, 3, 5); // Copy first 5 bytes

                _fcEvent.Reset();
                if (!WriteCanFrame(txCanId, canData, 8))
                {
                    return false;
                }

                // Wait for Flow Control (FC)
                if (!_fcEvent.WaitOne(1500) || _fcState != FlowControlState.ContinueToSend)
                {
                    Ediabas?.LogString(EdiabasNet.EdLogLevel.Ifh, "ISO-TP error: Timeout/abort waiting for Flow Control. Returning true to trigger IFH_0009.");
                    return true;
                }

                // Reset Flow Control State
                _fcState = FlowControlState.None;

                // Send Consecutive Frames (CF)
                int bytesSent = 5;
                byte seq = 1;
                int stMinMs = 0;
                if (_fcSTmin >= 0x01 && _fcSTmin <= 0x7F)
                {
                    stMinMs = _fcSTmin;
                }
                else if (_fcSTmin >= 0xF1 && _fcSTmin <= 0xF9)
                {
                    stMinMs = 1; // 1ms for sub-millisecond values as approximation
                }

                while (bytesSent < udsPayloadLen)
                {
                    int toSend = Math.Min(udsPayloadLen - bytesSent, 6);
                    canData = new byte[8];
                    canData[0] = ecuAddr;
                    canData[1] = (byte)(0x20 | (seq & 0x0F));
                    Array.Copy(udsPayload, bytesSent, canData, 2, toSend);

                    if (!WriteCanFrame(txCanId, canData, 8))
                    {
                        return false;
                    }

                    bytesSent += toSend;
                    seq++;
                    if (stMinMs > 0)
                    {
                        Thread.Sleep(stMinMs);
                    }
                }

                return true;
            }
        }

        // Flow control wait states
        private enum FlowControlState
        {
            None,
            ContinueToSend,
            Wait,
            Abort
        }

        private static volatile FlowControlState _fcState = FlowControlState.None;

        // Interface ReceiveData
        public static bool InterfaceReceiveData(byte[] receiveData, int offset, int length, int timeout, int timeoutTelEnd, EdiabasNet ediabasLog)
        {
            long startTick = Stopwatch.GetTimestamp();
            long maxTicks = timeout * (Stopwatch.Frequency / 1000);

            while ((Stopwatch.GetTimestamp() - startTick) < maxTicks)
            {
                lock (_receivedQueueLock)
                {
                    if (_receivedUdsQueue.Count > 0)
                    {
                        byte[] data = _receivedUdsQueue.Peek();
                        if (data.Length >= length)
                        {
                            Array.Copy(data, 0, receiveData, offset, length);
                            if (data.Length == length)
                            {
                                _receivedUdsQueue.Dequeue();
                            }
                            else
                            {
                                byte[] remainder = new byte[data.Length - length];
                                Array.Copy(data, length, remainder, 0, remainder.Length);
                                _receivedUdsQueue.Dequeue();
                                List<byte[]> temp = new List<byte[]>(_receivedUdsQueue);
                                _receivedUdsQueue.Clear();
                                _receivedUdsQueue.Enqueue(remainder);
                                foreach (byte[] item in temp)
                                {
                                    _receivedUdsQueue.Enqueue(item);
                                }
                            }
                            return true;
                        }
                    }
                }
                _receiveDataEvent.WaitOne(10);
            }

            return false;
        }

        public static bool InterfacePurgeInBuffer()
        {
            lock (_receivedQueueLock)
            {
                _receivedUdsQueue.Clear();
            }
            lock (_isoTpRxLock)
            {
                _isoTpRxBuffer = null;
                _isoTpRxLength = 0;
            }
            _fcState = FlowControlState.None;

            if (_winusbHandle != IntPtr.Zero)
            {
                WinUsb_FlushPipe(_winusbHandle, 0x81);
            }
            return true;
        }

        private static void ReadThreadFunc()
        {
            byte[] buffer = new byte[20]; // sizeof(gs_host_frame)
            while (!_terminateReadThread)
            {
                if (_winusbHandle == IntPtr.Zero)
                {
                    Thread.Sleep(50);
                    continue;
                }

                uint read = 0;
                bool success = WinUsb_ReadPipe(_winusbHandle, 0x81, buffer, (uint)buffer.Length, out read, IntPtr.Zero);
                if (success)
                {
                    if (read == 20)
                    {
                        uint echo_id = (uint)(buffer[0] | (buffer[1] << 8) | (buffer[2] << 16) | (buffer[3] << 24));
                        uint canId = (uint)(buffer[4] | (buffer[5] << 8) | (buffer[6] << 16) | (buffer[7] << 24));
                        byte canDlc = buffer[8];
                        byte[] data = new byte[8];
                        Array.Copy(buffer, 12, data, 0, 8);

                        uint maskedCanId = canId & 0x1FFFFFFF;
                        bool isTargetCanId = false;
                        if (_canRxId != -1)
                        {
                            if (maskedCanId == (uint)_canRxId)
                            {
                                isTargetCanId = true;
                            }
                        }
                        else
                        {
                            if (_lastEcuAddr != 0 && maskedCanId == GetRxCanId())
                            {
                                isTargetCanId = true;
                            }
                        }

                        if (isTargetCanId)
                        {
                            ProcessReceivedCanFrame(data, canDlc, maskedCanId);
                        }
                    }
                }
                else
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 121) // 121 = ERROR_SEM_TIMEOUT
                    {
                        LogFormat("WinUsb_ReadPipe failed: {0}", err);
                    }
                    Thread.Sleep(1);
                }
            }
        }

        // Process received CAN frame and reassemble ISO-TP
        private static void ProcessReceivedCanFrame(byte[] data, byte dlc, uint rxCanId)
        {
            if (dlc < 2) return; // Must have at least ECU address and ISO-TP PCI byte

            byte type = (byte)(data[1] & 0xF0); // Shifted by 1

            if (type == 0x00)
            {
                // Single Frame (SF)
                int len = data[1] & 0x0F;
                if (len > 0 && len <= 6)
                {
                    byte[] udsResponse = new byte[len];
                    Array.Copy(data, 2, udsResponse, 0, len);
                    EnqueueUdsResponse(udsResponse, rxCanId, data[0]);
                }
            }
            else if (type == 0x10)
            {
                // First Frame (FF)
                int len = ((data[1] & 0x0F) << 8) | data[2];
                lock (_isoTpRxLock)
                {
                    _isoTpRxBuffer = new byte[len];
                    Array.Copy(data, 3, _isoTpRxBuffer, 0, Math.Min(len, 5));
                    _isoTpRxLength = Math.Min(len, 5);
                    _isoTpRxExpectedSeq = 1;
                }

                // Send Flow Control (FC) frame back
                byte[] fcFrame = new byte[8];
                byte ecuAddr = (byte)(rxCanId & 0xFF);

                fcFrame[0] = ecuAddr;
                fcFrame[1] = 0x30; // FlowStatus = Continue to Send
                fcFrame[2] = 0x00; // BlockSize = 0 (receive all remaining frames in one block)
                fcFrame[3] = 0x02; // STmin = 2ms (request 2ms separation time to prevent frame drops)
                WriteCanFrame(GetTxCanId(data[0]), fcFrame, 8);
            }
            else if (type == 0x20)
            {
                // Consecutive Frame (CF)
                byte seq = (byte)(data[1] & 0x0F);
                lock (_isoTpRxLock)
                {
                    if (_isoTpRxBuffer != null)
                    {
                        if (seq == (_isoTpRxExpectedSeq & 0x0F))
                        {
                            int remaining = _isoTpRxBuffer.Length - _isoTpRxLength;
                            int toCopy = Math.Min(remaining, 6);
                            Array.Copy(data, 2, _isoTpRxBuffer, _isoTpRxLength, toCopy);
                            _isoTpRxLength += toCopy;
                            _isoTpRxExpectedSeq++;

                            if (_isoTpRxLength >= _isoTpRxBuffer.Length)
                            {
                                EnqueueUdsResponse(_isoTpRxBuffer, rxCanId, data[0]);
                                _isoTpRxBuffer = null;
                                _isoTpRxLength = 0;
                            }
                        }
                        else
                        {
                            _isoTpTpRxBufferClean();
                        }
                    }
                }
            }
            else if (type == 0x30)
            {
                // Flow Control (FC) frame received (when we are transmitting to ECU)
                byte flowStatus = (byte)(data[1] & 0x0F);
                _fcSTmin = data[3];
                if (flowStatus == 0)
                {
                    _fcState = FlowControlState.ContinueToSend;
                }
                else if (flowStatus == 1)
                {
                    _fcState = FlowControlState.Wait;
                }
                else
                {
                    _fcState = FlowControlState.Abort;
                }
                _fcEvent.Set();
            }
        }

        private static void _isoTpTpRxBufferClean()
        {
            _isoTpRxBuffer = null;
            _isoTpRxLength = 0;
        }

        private static void EnqueueUdsResponse(byte[] udsData, uint rxCanId, byte testerAddr = 0)
        {
            // Format for EdInterfaceObd (BMW Fast format):
            //   [Length, Target (Tester), Source (ECU), payload..., checksum]
            byte target = testerAddr != 0 ? testerAddr : (byte)(GetTxCanId(testerAddr) & 0xFF);
            byte srcAddr = (byte)(rxCanId & 0xFF);

            int N = udsData.Length;
            byte[] formatted;

            if (N <= 63)
            {
                formatted = new byte[N + 4];
                formatted[0] = (byte)(0x80 + N);
                formatted[1] = target;
                formatted[2] = srcAddr;
                Array.Copy(udsData, 0, formatted, 3, N);
            }
            else if (N <= 255)
            {
                formatted = new byte[N + 5];
                formatted[0] = 0x80;
                formatted[1] = target;
                formatted[2] = srcAddr;
                formatted[3] = (byte)N;
                Array.Copy(udsData, 0, formatted, 4, N);
            }
            else
            {
                formatted = new byte[N + 7];
                formatted[0] = 0x80;
                formatted[1] = target;
                formatted[2] = srcAddr;
                formatted[3] = 0x00;
                formatted[4] = (byte)(N >> 8);
                formatted[5] = (byte)(N & 0xFF);
                Array.Copy(udsData, 0, formatted, 6, N);
            }

            byte sum = 0;
            for (int i = 0; i < formatted.Length - 1; i++)
            {
                sum += formatted[i];
            }
            formatted[formatted.Length - 1] = sum;

            lock (_receivedQueueLock)
            {
                _receivedUdsQueue.Enqueue(formatted);
            }
            _receiveDataEvent.Set();
        }
    }
}
