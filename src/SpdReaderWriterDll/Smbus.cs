/*
    Arduino based EEPROM SPD reader and writer
   ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~
   For overclockers and PC hardware enthusiasts

   Repos:   https://github.com/1a2m3/SPD-Reader-Writer
   Support: https://forums.evga.com/FindPost/3053544
   Donate:  https://paypal.me/mik4rt3m

*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using UInt8 = System.Byte;

namespace SpdReaderWriterDll {

    /// <summary>
    /// SMBus class
    /// </summary>
    public class Smbus : IDisposable {

        /// <summary>
        /// Kernel Driver instance
        /// </summary>
        public WinRing0 Driver {
            get => _driver;
            set => _driver = value;
        }
        internal static WinRing0 _driver;

        /// <summary>
        /// Kernel Driver version
        /// </summary>
        public string Version {
            get {
                byte[] v = new byte[4];
                if (_driver != null && _driver.IsReady) {
                    _driver.GetDriverVersion(ref v[0], ref v[1], ref v[2], ref v[3]);
                }

                return $"{v[0]}.{v[1]}.{v[2]}.{v[3]}";
            }
        }

        /// <summary>
        /// Device info struct
        /// </summary>
        public struct DeviceInfo {
            public VendorId VendorId;
            public DeviceId DeviceId;
        }
        public DeviceInfo Info {
            get => _info;
            set => _info = value;
        }
        private static DeviceInfo _info;

        /// <summary>
        /// SMBus bus number
        /// </summary>
        public byte BusNumber {
            get => _busNumber;
            set {
                _busNumber = value;
                // Reset Eeprom page when bus is set
                Eeprom.ResetPageAddress(this);
            }
        }
        private static byte _busNumber;

        /// <summary>
        /// Last set slave address
        /// </summary>
        public UInt8 I2CAddress;

        /// <summary>
        /// Maximum SPD size on this device
        /// </summary>
        public UInt16 MaxSpdSize;

        /// <summary>
        /// SPD BIOS write disable state (ICH/PCH only)
        /// </summary>
        public bool SpdWriteDisabled;

        /// <summary>
        /// PCI device instance
        /// </summary>
        public PciDevice pciDevice {
            get => _pciDevice;
            set => _pciDevice = value;
        }
        private static PciDevice _pciDevice;

        /// <summary>
        /// IO port instance
        /// </summary>
        public IoPort ioPort {
            get => _ioPort;
            set => _ioPort = value;
        }
        private static IoPort _ioPort;

        /// <summary>
        /// Initialize SMBus with default settings
        /// </summary>
        public Smbus() {
            Initialize();
        }

        /// <summary>
        /// SMBus instance destructor
        /// </summary>
        ~Smbus() {
            Dispose();
        }

        /// <summary>
        /// Disposes SMBus instance
        /// </summary>
        public void Dispose() {
            ioPort     = null;
            pciDevice  = null;
            Info       = default;
            Driver     = null;

            if (!IsRunning) {
                I2CAddress       = 0;
                SMBuses          = null;
                MaxSpdSize       = 0;
                IsConnected      = false;
                SpdWriteDisabled = false;
            }
        }

        /// <summary>
        /// SMBus name
        /// </summary>
        /// <returns>Human readable SMBus name in a form of platform vendor name and chipset model name</returns>
        public override string ToString() {
            return $"{_info.VendorId} {_info.DeviceId}";
        }

        /// <summary>
        /// Describes SMBus state
        /// </summary>
        public bool IsRunning => (ioPort != null || pciDevice != null) && SMBuses.Length > 0 && Addresses.Length > 0;

        /// <summary>
        /// Describes SMBus connection state
        /// </summary>
        public bool IsConnected;

        /// <summary>
        /// Available SMBuses
        /// </summary>
        public UInt8[] SMBuses;

        /// <summary>
        /// Available slave addresses on selected bus
        /// </summary>
        public UInt8[] Addresses;
        
        /// <summary>
        /// Platform Vendor ID
        /// </summary>
        public enum VendorId : UInt16 {
            AMD    = 0x1022,
            ATI    = 0x1002, // Former ATI vendor ID which is now owned by AMD, who has 2 vendor IDs
            Intel  = 0x8086,
            Nvidia = 0x10DE,
        }

        /// <summary>
        /// Intel: ICH/PCH device ID (LPC/eSPI controller or ISA bridge)
        /// AMD:   SMBus device ID
        /// </summary>
        public enum DeviceId : UInt16 {

            // Old pre-PCH hardware

            #region ICH
            ICH      = 0x2410,
            ICH0     = 0x2420,
            ICH2     = 0x2440,
            ICH2M    = 0x244C,
            ICH3     = 0x2480,
            ICH3M    = 0x248C,
            ICH4     = 0x24C0,
            ICH4M    = 0x24CC,
            CICH     = 0x2450,
            ICH5     = 0x24D0,
            ICH6M    = 0x2641,
            ICH6W    = 0x2642,
            ICH7DH   = 0x27B0,
            ICH7     = 0x27B8,
            ICH7M    = 0x27B9,
            ICH7MDH  = 0x27BD,
            ICH8     = 0x2810,
            ICH8ME   = 0x2811,
            ICH8DH   = 0x2812,
            ICH8DO   = 0x2814,
            ICH8M    = 0x2815,
            ICH9DH   = 0x2912,
            ICH9DO   = 0x2914,
            ICH9R    = 0x2916,
            ICH9ME   = 0x2917,
            ICH9     = 0x2918,
            ICH9M    = 0x2919,
            ICH10DO  = 0x3A14,
            ICH10R   = 0x3A16,
            ICH10    = 0x3A18,
            ICH10D   = 0x3A1A,
            #endregion

            // DDR3

            #region LGA1156
            H55      = 0x3B06,
            H57      = 0x3B08,
            P55      = 0x3B02,
            Q57      = 0x3B0A,
            #endregion

            #region LGA1155
            B65      = 0x1C50,
            B75      = 0x1E49,
            H61      = 0x1C5C,
            H67      = 0x1C4A,
            H77      = 0x1E4A,
            P67      = 0x1C46,
            Q65      = 0x1C4C,
            Q67      = 0x1C4E,
            Q75      = 0x1E48,
            Q77      = 0x1E47,
            Z68      = 0x1C44,
            Z75      = 0x1E46,
            Z77      = 0x1E44,
            #endregion

            #region LGA1150
            B85      = 0x8C50,
            H81      = 0x8C5C,
            H87      = 0x8C4A,
            H97      = 0x8CC6,
            Q85      = 0x8C4C,
            Q87      = 0x8C4E,
            Z87      = 0x8C44,
            Z97      = 0x8CC4,
            #endregion

            #region MOBILE 5/6/7/8/9 Series
            HM55     = 0x3B09,
            HM57     = 0x3B0B,
            HM65     = 0x1C49,
            HM67     = 0x1C4B,
            HM70     = 0x1E5E,
            HM75     = 0x1E5D,
            HM76     = 0x1E59,
            HM77     = 0x1E57,
            HM86     = 0x8C49,
            HM87     = 0x8C4B,
            HM97     = 0x8CC3,
            NM10     = 0x27BC,
            NM70     = 0x1E5F,
            PM55     = 0x3B03,
            QM57     = 0x3B07,
            QM67     = 0x1C4F,
            QM77     = 0x1E55,
            QM87     = 0x8C4F,
            QS57     = 0x3B0F,
            QS67     = 0x1C4D,
            QS77     = 0x1E56,
            UM67     = 0x1C47,
            UM77     = 0x1E58,
            #endregion

            // DDR4

            #region LGA1151
            B150  = 0xA148,
            B250  = 0xA2C8,
            B360  = 0xA308,
            B365  = 0xA2CC,
            C232  = 0xA14A,
            C236  = 0xA149,
            C242  = 0xA30A,
            C246  = 0xA309,
            CM236 = 0xA150,
            CM238 = 0xA154,
            CM246 = 0xA30E,
            H110  = 0xA143,
            H170  = 0xA144,
            H270  = 0xA2C4,
            H310  = 0xA303,
            H310D = 0x438E,
            H310M = 0xA2CA,
            H370  = 0xA304,
            HM170 = 0xA14E,
            HM175 = 0xA152,
            HM370 = 0xA30D, // aka HM470
            Q150  = 0xA147,
            Q170  = 0xA146,
            Q250  = 0xA2C7,
            Q270  = 0xA2C6,
            Q370  = 0xA306,
            QM170 = 0xA14D,
            QM175 = 0xA153,
            QM370 = 0xA30C,
            Z170  = 0xA145,
            Z270  = 0xA2C5,
            Z370  = 0xA2C9,
            Z390  = 0xA305,
            #endregion

            #region LGA1200
            B460  = 0xA3C8,
            B560  = 0x4387,
            C252  = 0x438C,
            C256  = 0x438D,
            H410  = 0xA3DA,
            H470  = 0x0684,
            H510  = 0x4388,
            H570  = 0x4386,
            Q470  = 0x0687,
            Q570  = 0x4384,
            W480  = 0x0697,
            W580  = 0x438F,
            Z490  = 0x0685,
            Z590  = 0x4385,
            #endregion

            #region LGA2066
            X299 = 0xA2D2, // CPU SMBus x2 (8086h:2085h)
            C422 = 0xA2D3, // Same as X299
            #endregion

            // AMD
            FCH     = 0x790B, // AM4/ZEN
            Hudson2 = 0x780B, // FM1/FM2(+)

            // Nvidia
            nForce2        = 0x0064,
            nForce2_Ultra  = 0x0084,
            nForce3_Pro150 = 0x00D4,
            nForce3_250Gb  = 0x00E4,
            nForce4        = 0x0052,
            nForce4_MCP04  = 0x0034,
            MCP51          = 0x0264,
            MCP55          = 0x0368,
            MCP61          = 0x03EB,
            MCP65          = 0x0446,
            MCP67          = 0x0542,
            MCP73          = 0x07D8,
            MCP78S         = 0x0752,
            MCP79          = 0x0AA2,
        }

        /// <summary>
        /// Intel ICH/PCH and AMD FCH SMBus controller register offsets
        /// </summary>
        public struct DefaultSmbusRegister {

            /// <summary>
            /// Host status
            /// </summary>
            public const byte Status  = 0x00;

            /// <summary>
            /// Host control
            /// </summary>
            /// <remarks>Used to execute <see cref="SmbusCmd"/> after setting registers <see cref="Address"/>,
            /// <see cref="HostCmd"/>, (and <see cref="Data0"/>, if <see cref="SmbusAccessMode.Write"/> mode is used)</remarks>
            public const byte Control = 0x02;

            /// <summary>
            /// Host command
            /// </summary>
            /// <remarks>This registers is used to specify offset</remarks>
            public const byte HostCmd = 0x03;

            /// <summary>
            /// Transmit slave address
            /// </summary>
            /// <remarks>Bits 7-1: i2c address;
            /// bit 0: r/w mode (1 = <see cref="SmbusAccessMode.Read"/>, 0 = <see cref="SmbusAccessMode.Write"/>)</remarks>
            /// <example>Set to 0xA1 to read from 0x50, or 0xA0 to write to 0x50</example>
            public const byte Address = 0x04;

            /// <summary>
            /// Data 0
            /// </summary>
            /// <remarks>Input for <see cref="SmbusAccessMode.Write"/>,
            /// or output for <see cref="SmbusAccessMode.Read"/> operation</remarks>
            public const byte Data0   = 0x05;
        }

        /// <summary>
        /// Intel CPU SMBus Device ID
        /// </summary>
        public enum SkylakeXDeviceId : UInt16 {
            // LGA 2066 SKL-X & CLX-X IMC Smbus
            CpuImcSmbus = 0x2085,
        }

        /// <summary>
        /// Intel Skylake-X CPU SMBus controller register offsets
        /// </summary>
        private struct SkylakeXSmbusRegister {
            public const byte Address    = 0x9D;
            public const byte Offset     = 0x9C;
            public const byte Input      = 0xB6;
            public const byte Command    = 0x9E;
            public const byte Output     = 0xB4;
            public const byte Status     = 0xA8;
            public const byte DimmConfig = 0x94;
        }

        /// <summary>
        /// Describes Skylake-X CPU SMBus controller status codes at <see cref="SkylakeXSmbusRegister.Status">status register</see>
        /// </summary>
        private struct SkylakeXSmbusStatus {
            public const byte Ready    = 0b00000000;
            public const byte Complete = 0b00000100;
            public const byte Busy     = 0b00000001;
            public const byte Ack      = 0b00000000;
            public const byte Nack     = 0b00000010;
        }

        /// <summary>
        /// Nvidia Smbus register offsets
        /// </summary>
        private struct NvidiaSmbusRegister {
            public const byte Protocol    = 0x00;
            public const byte Status      = 0x01;
            public const byte Address     = 0x02;
            public const byte Command     = 0x03; // Offset
            public const byte Data        = 0x04; // Input (write) or output (read)
            public const byte ByteCount   = 0x24; // Number of data bytes
            public const byte Control     = 0x3E; // Control register
            public const byte AbortStatus = 0x3C; // register used to check the status of the abort command
        }

        /// <summary>
        /// Nvidia Smbus protocol commands for the <see cref="NvidiaSmbusRegister.Protocol"/> register
        /// </summary>
        private struct NvidiaSmbusProtocol {
            public const byte Write     = 0x00;
            public const byte Read      = 0x01;

            public const byte Quick     = 0x02;
            public const byte Byte      = 0x04;
            public const byte ByteData  = 0x06;
            public const byte WordData  = 0x08;
            public const byte BlockData = 0x0A;

            public const byte Abort     = 0x20;

            public const byte Pec       = 0x80;
        }

        /// <summary>
        /// Describes Nvidia Smbus status codes at <see cref="NvidiaSmbusRegister.Status">status register</see>
        /// </summary>
        private struct NvidiaSmbusStatus {
            public const byte Done    = 0x80;
            public const byte Invalid = 0x10;
            public const byte Error   = 0x1F;
        }

        /// <summary>
        /// Check if the device ID is supported
        /// </summary>
        /// <param name="deviceId">Chipset DeviceID</param>
        /// <returns><see langword="true"/> if <paramref name="deviceId"/> is present in the <see cref="DeviceId"/> enum</returns>
        private static bool CheckChipsetSupport(DeviceId deviceId) {
            return Enum.IsDefined(typeof(DeviceId), deviceId);
        }

        /// <summary>
        /// Check if the device ID belongs to Skylake-X or Cascade Lake-X platform
        /// </summary>
        /// <param name="deviceId">Chipset DeviceID</param>
        /// <returns><see langword="true"/> if <paramref name="deviceId"/> belongs to Skylake-X or Cascade Lake-X platform</returns>
        private static bool IsSkylakeX(DeviceId deviceId) {

            DeviceId[] SkylakeX = {
                DeviceId.X299,
                DeviceId.C422,
            };

            return SkylakeX.Contains(deviceId);
        }

        /// <summary>
        /// Gets platform type
        /// </summary>
        private static Platform PlatformType {
            get {
                if (IsSkylakeX(_info.DeviceId)) {
                    return Platform.SkylakeX;
                }

                if (CheckChipsetSupport(_info.DeviceId)) {
                    return Platform.Default;
                }

                return Platform.Unknown;
            }
        }

        /// <summary>
        /// Platform type
        /// </summary>
        private enum Platform : byte {

            /// <summary>
            /// Unknown or unsupported platform type
            /// </summary>
            Unknown,

            /// <summary>
            /// Intel ICH/PCH, AMD FCH, and Nvidia MCP platforms
            /// </summary>
            Default,

            /// <summary>
            /// Skylake X (incl. Refresh) and Cascade Lake X platforms
            /// </summary>
            SkylakeX,
        }

        /// <summary>
        /// Initializes SMBus controller class
        /// </summary>
        private void Initialize() {

            try {
                Driver = new WinRing0();

                if (Driver.IsReady) {
                    Info = GetDeviceInfo();
                }
            }
            catch (Exception e) {
                throw new Exception($"{nameof(WinRing0)} initialization failure: {e.Message}");
            }

            switch (Info.VendorId) {
                // Skylake-X
                case VendorId.Intel when PlatformType == Platform.SkylakeX:
                    // Locate CPU SMBus controller
                    pciDevice = new PciDevice(PciDevice.FindDeviceById((UInt16)Info.VendorId, (UInt16)SkylakeXDeviceId.CpuImcSmbus));
                    break;
                // ICH/PCH
                case VendorId.Intel:
                case VendorId.Nvidia:
                    if (PlatformType == Platform.Default) {

                        // Locate ICH/PCH SMBus controller
                        pciDevice = new PciDevice(PciDevice.FindDeviceByClass(PciDevice.BaseClass.Serial, PciDevice.SubClass.Smbus));

                        // Read IO port address and info
                        UInt16 ioPortAddress = pciDevice.ReadWord(PciDevice.RegisterOffset.BaseAddress[4]);

                        // Check SPD write disable bit
                        SpdWriteDisabled = Data.GetBit(pciDevice.ReadByte(0x40), 4);

                        // Check if SMbus is port mapped
                        if ((ioPortAddress & 1) == 1) {

                            // Initialize new SMBus IO port instance
                            ioPort = new IoPort((UInt16)(ioPortAddress & 0xFFFE));
                        }
                    }

                    break;

                case VendorId.AMD:
                    pciDevice = new PciDevice(PciDevice.FindDeviceByClass(PciDevice.BaseClass.Serial, PciDevice.SubClass.Smbus));

                    // AMD AM4, AM1, FM1, FM2(+)
                    if ((pciDevice.DeviceId == (UInt16)DeviceId.FCH && pciDevice.RevisionId >= 0x49) ||
                        (pciDevice.DeviceId == (UInt16)DeviceId.Hudson2 && pciDevice.RevisionId >= 0x41)) {

                        // PMIO registers accessible via IO ports
                        const UInt16 SB800_PIIX4_SMB_IDX = 0xCD6;
                        const UInt16 SB800_PIIX4_SMB_DAT = 0xCD7;

                        byte smb_en = 0x00;

                        IoPort _ioPort = new IoPort();

                        _ioPort.WriteByte(SB800_PIIX4_SMB_IDX, smb_en);
                        byte smba_en_lo = _ioPort.ReadByte(SB800_PIIX4_SMB_DAT);

                        _ioPort.WriteByte(SB800_PIIX4_SMB_IDX, (byte)(smb_en + 1));
                        byte smba_en_hi = _ioPort.ReadByte(SB800_PIIX4_SMB_DAT);

                        byte smb_en_status = (byte)(smba_en_lo & 0x10);

                        UInt16 piix4_smba; // Primary bus

                        if (smb_en_status > 0) {
                            piix4_smba = (UInt16)(smba_en_hi << 8); // 0x0B00

                            if (piix4_smba != 0x00) {
                                ioPort = new IoPort(piix4_smba);
                            }
                        }
                    }

                    break;
            }

            // Common properties
            BusNumber         = 0;
            byte[] scanResult = Scan();
            Addresses         = scanResult;
            MaxSpdSize        = scanResult.Length > 0 ? GetMaxSpdSize(scanResult[0]) : (UInt16)Spd.DataLength.UNKNOWN;
            SMBuses           = FindBus();
        }

        /// <summary>
        /// Get platform information
        /// </summary>
        /// <returns>Platform and chipset Device/Vendor ID</returns>
        public static DeviceInfo GetDeviceInfo() {

            DeviceInfo result  = new DeviceInfo();
            PciDevice platform = new PciDevice();

            result.VendorId = (VendorId)platform.VendorId;

            switch (result.VendorId) {
                case VendorId.Intel:
                    // Find ISA bridge to get chipset ID
                    try {
                        UInt32 isa = PciDevice.FindDeviceByClass(PciDevice.BaseClass.Bridge, PciDevice.SubClass.Isa);
                        result.DeviceId = (DeviceId)new PciDevice(isa).DeviceId;
                    }
                    catch {
                        result.DeviceId = default;
                    }

                    break;

                case VendorId.AMD:
                case VendorId.Nvidia:
                    UInt32 smbus = PciDevice.FindDeviceByClass(PciDevice.BaseClass.Serial, PciDevice.SubClass.Smbus);
                    result.DeviceId = (DeviceId)new PciDevice(smbus).DeviceId;
                    break;
            }

            return result;
        }

        /// <summary>
        /// Locates available SMBuses on the device
        /// </summary>
        /// <returns>An array of bytes containing SMBus numbers</returns>
        public byte[] FindBus() {

            UInt8 originalBus = BusNumber;

            try {
                Queue<byte> result = new Queue<byte>();

                for (byte i = 0; i <= 1; i++) {
                    BusNumber = i;
                    if (TryScan()) {
                        result.Enqueue(i); // The bus is valid
                    }
                }

                return result.ToArray();
            }
            catch {
                return new byte[0];
            }
            finally {
                BusNumber = originalBus;
            }
        }

        /// <summary>
        /// Scan SMBus for available slave devices
        /// </summary>
        /// <returns><see langword="true"/> if <see cref="BusNumber"/> has at least one slave device present</returns>
        public bool TryScan() {
            for (byte i = 0x50; i <= 0x57; i++) {
                if (ProbeAddress(i)) {
                    return true;
                }
            }

            return Scan(this, minimumResults: true).Length > 0;
        }

        /// <summary>
        /// Validates slave address by reading first byte from it
        /// </summary>
        /// <param name="slaveAddress">Slave address</param>
        /// <returns><see langword="true"/> if <paramref name="slaveAddress"/> exists on the bus</returns>
        public bool ProbeAddress(byte slaveAddress) {
            return ReadByte(this, slaveAddress);
        }

        /// <summary>
        /// Scan SMBus for available slave devices
        /// </summary>
        /// <returns>An array of available bus addresses</returns>
        public UInt8[] Scan() {
            return Scan(this, minimumResults: false);
        }

        /// <summary>
        /// Scan SMBus for available slave devices
        /// </summary>
        /// <param name="bitmask">Set to <see langword="true"/> to enable bitmask result</param>
        /// <returns>
        /// A bitmask value representing available bus addresses.
        /// <example>Bit 0 is address 80, bit 1 is address 81, and so on.</example>
        /// </returns>
        public byte Scan(bool bitmask) {
            byte result = 0;

            if (!bitmask) {
                throw new ArgumentException("Invalid use of method argument " + nameof(bitmask));
            }

            byte[] scanResult = Scan();

            foreach (byte address in scanResult) {
                result = Data.SetBit(result, (byte)(address - 0x50), true);
            }

            return result;
        }

        /// <summary>
        /// Scan SMBus for available slave devices
        /// </summary>
        /// <param name="device">SMBus instance</param>
        /// <param name="minimumResults">Set to <see langword="true"/> to stop scanning once at least one slave address is found,
        /// or <see langword="false"/> to scan the entire range</param>
        /// <returns>An array of found bus addresses on <see cref="BusNumber"/></returns>
        private UInt8[] Scan(Smbus device, bool minimumResults) {

            Queue<byte> result = new Queue<byte>();

            for (byte i = 0; i <= 7; i++) {
                try {
                    if (ReadByte(device, (byte)(i + 0x50))) {
                        result.Enqueue((byte)(i + 0x50));
                        if (minimumResults) {
                            break;
                        }
                    }
                }
                catch {
                    continue;
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// Attempts to read from specified slave address
        /// </summary>
        /// <param name="device">SMBus instance</param>
        /// <param name="slaveAddress">Slave address</param>
        /// <returns><see langword="true"/> if <paramref name="device"/> responds with
        /// <see cref="SmbStatus.Success"/> or <see cref="SmbStatus.Ready"/></returns>
        public static bool ReadByte(Smbus device, byte slaveAddress) {

            SmbusData busData = new SmbusData {
                BusNumber   = device.BusNumber,
                Address     = slaveAddress,
                AccessMode  = SmbusAccessMode.Read,
                DataCommand = SmbusDataCommand.Byte,
            };

            try {
                return ExecuteCommand(ref busData);
            }
            catch {
                return false;
            }
        }

        /// <summary>
        /// Reads a single byte from specified offset of specified slave address (disregarding page address)
        /// </summary>
        /// <param name="device">SMBus instance</param>
        /// <param name="slaveAddress">Slave address</param>
        /// <param name="offset">Byte position</param>
        /// <returns>Byte value read from the device</returns>
        public static byte ReadByte(Smbus device, byte slaveAddress, UInt16 offset) {

            SmbusData busData = new SmbusData {
                BusNumber   = device.BusNumber,
                Address     = slaveAddress,
                Offset      = offset,
                AccessMode  = SmbusAccessMode.Read,
                DataCommand = SmbusDataCommand.ByteData,
            };

            try {
                ExecuteCommand(ref busData);
                return busData.Output;
            }
            catch {
                throw new IOException($"Read error: {device}:{slaveAddress}:{offset}");
            }
        }

        /// <summary>
        /// Writes DNC data to the device
        /// </summary>
        /// <param name="device">SMBus instance</param>
        /// <param name="slaveAddress">Slave address</param>
        /// <returns><see langword="true"/> if <paramref name="device"/> responds with
        /// <see cref="SmbStatus.Success"/> or <see cref="SmbStatus.Ready"/></returns>
        public static bool WriteByte(Smbus device, UInt8 slaveAddress) {

            SmbusData busData = new SmbusData {
                BusNumber   = device.BusNumber,
                Address     = slaveAddress,
                AccessMode  = SmbusAccessMode.Write,
                DataCommand = SmbusDataCommand.Byte,
            };

            try {
                return ExecuteCommand(ref busData);
            }
            catch {
                return false;
            }
        }

        /// <summary>
        /// Write specified byte value to specified offset
        /// </summary>
        /// <param name="device">SMBus instance</param>
        /// <param name="slaveAddress">Slave address</param>
        /// <param name="offset">Byte position</param>
        /// <param name="value">Byte value</param>
        /// <returns><see langwod="true"/> if <paramref name="device"/> responds with
        /// <see cref="SmbStatus.Success"/> or <see cref="SmbStatus.Ready"/></returns>
        public static bool WriteByte(Smbus device, UInt8 slaveAddress, UInt16 offset, byte value) {

            SmbusData busData = new SmbusData {
                BusNumber   = device.BusNumber,
                Address     = slaveAddress,
                Offset      = offset,
                Input       = value,
                AccessMode  = SmbusAccessMode.Write,
                DataCommand = SmbusDataCommand.ByteData,
            };

            try {
                return ExecuteCommand(ref busData);
            }
            catch {
                return false;
            }
        }

        /// <summary>
        /// Programs SMBus controller with input data 
        /// </summary>
        /// <param name="smbusData">SMBus input data</param>
        /// <returns><see langword="true"/> if SMBus doesn't return
        /// <see cref="SmbStatus.Error"/> or <see cref="SmbStatus.Timeout"/></returns>
        private static bool ExecuteCommand(ref SmbusData smbusData) {

            if (PlatformType == Platform.Unknown) {
                return false;
            }

            if (PlatformType == Platform.SkylakeX) {
                // Set input for writing
                if (smbusData.AccessMode == SmbusAccessMode.Write) {
                    _pciDevice.WriteByte(
                        offset : (byte)(SkylakeXSmbusRegister.Input + smbusData.BusNumber * 4),
                        value  : smbusData.Input);
                }

                // Set slave address, offset, and Execute command
                _pciDevice.WriteDword(
                    offset : (UInt8)(SkylakeXSmbusRegister.Offset + smbusData.BusNumber * 4),
                    value  : (UInt32)(
                        // Enable byte read
                        0x20 << 24 |
                        // Execute command
                        (SmbusCmd.CmdByteData | SmbusCmd.Start) << 16 |
                        // Address and R/W mode
                        (smbusData.Address | (smbusData.AccessMode == SmbusAccessMode.Write ? 1 << 7 : 0)) << 8 |
                        // Offset
                        (byte)smbusData.Offset
                        )
                    );

                // Wait after writing
                if (smbusData.AccessMode == SmbusAccessMode.Write) {
                    Thread.Sleep(Eeprom.ValidateEepromAddress(smbusData.Address) 
                        ? ExecutionDelay.WriteDelay 
                        : ExecutionDelay.WaitDelay);
                }

                // Wait for completion
                if (!WaitForStatus(new[] { SmbStatus.Ready, SmbStatus.Success, SmbStatus.Error }, 1000)) {
                    smbusData.Status = SmbStatus.Timeout;
                    return false;
                }

                // Set status
                smbusData.Status = GetBusStatus();

                // Read and assign output if read mode is specified
                if (smbusData.Status == SmbStatus.Error) {
                    return false;
                }

                if (smbusData.AccessMode == SmbusAccessMode.Read) {
                    smbusData.Output = _pciDevice.ReadByte(
                        offset : (byte)(SkylakeXSmbusRegister.Output + smbusData.BusNumber * 4));
                }
            }
            else if (PlatformType == Platform.Default && _info.VendorId == VendorId.Nvidia) {

                if (smbusData.BusNumber > 0) {
                    return false;
                }

                // Set Smbus address
                _ioPort.WriteByte(
                    offset : NvidiaSmbusRegister.Address, 
                    value  : (UInt8)(smbusData.Address << 1));

                // Set offset
                _ioPort.WriteByte(
                    offset : NvidiaSmbusRegister.Command,
                    value  : (UInt8)smbusData.Offset);

                // Set data byte register if Write mode is set
                if (smbusData.AccessMode == SmbusAccessMode.Write) {
                    _ioPort.WriteByte(
                        offset: NvidiaSmbusRegister.Data,
                        value: smbusData.Input);
                }

                // Protocol command
                byte protocolCmd = 0x00;

                // Check access mode and set protocol mode bit accordingly
                protocolCmd |= smbusData.AccessMode == SmbusAccessMode.Read
                    ? NvidiaSmbusProtocol.Read
                    : NvidiaSmbusProtocol.Write;

                // Command type
                switch (smbusData.DataCommand) {
                    case SmbusDataCommand.Quick:
                        protocolCmd |= NvidiaSmbusProtocol.Quick;
                        break;
                    case SmbusDataCommand.Byte:
                        protocolCmd |= NvidiaSmbusProtocol.Byte;
                        break;
                    case SmbusDataCommand.ByteData:
                    default:
                        protocolCmd |= NvidiaSmbusProtocol.ByteData;
                        break;
                    case SmbusDataCommand.WordData:
                        protocolCmd |= NvidiaSmbusProtocol.WordData;
                        break;
                }

                // Execute command
                _ioPort.WriteByte(
                    offset : NvidiaSmbusRegister.Protocol, 
                    value  : protocolCmd);

                // Wait after writing
                if (smbusData.AccessMode == SmbusAccessMode.Write) {
                    Thread.Sleep(Eeprom.ValidateEepromAddress(smbusData.Address)
                        ? ExecutionDelay.WriteDelay * 2
                        : ExecutionDelay.WaitDelay);
                }

                // Wait for completion
                if (!WaitForStatus(statuses: new[] { SmbStatus.Success, SmbStatus.Error }, 1000)) {
                    smbusData.Status = SmbStatus.Timeout;
                    return false;
                }

                // Set status
                smbusData.Status = GetBusStatus();

                if (smbusData.Status == SmbStatus.Error) {
                    return false;
                }

                // Read and assign output if read mode is specified
                if (smbusData.AccessMode == SmbusAccessMode.Read) {
                    smbusData.Output = _ioPort.ReadByte(NvidiaSmbusRegister.Data);
                }
            }
            else if (PlatformType == Platform.Default && _info.VendorId != VendorId.Nvidia) {
                // These platforms don't support multiple SMbuses
                if (smbusData.BusNumber > 0 && _info.VendorId == VendorId.Intel) {
                    return false;
                }

                // Alternative AMD SMBus
                UInt8 portOffset = 0;

                if (_info.VendorId == VendorId.AMD) {
                    portOffset = (UInt8)(_busNumber * 20);
                }

                // Clear status bitmask to reset status
                byte clearStatusMask = SmbusStatus.Interrupt |
                                       SmbusStatus.DeviceErr |
                                       SmbusStatus.BusCollision |
                                       SmbusStatus.Failed;

                // Reset status 
                _ioPort.WriteByte(
                    offset : (UInt8)(DefaultSmbusRegister.Status + portOffset),
                    value  : clearStatusMask);

                // Wait for ready status
                if (!WaitForStatus(SmbStatus.Ready, 1000)) {
                    smbusData.Status = SmbStatus.Timeout;
                    return false;
                }

                // Set slave address
                _ioPort.WriteByte(
                    offset : (UInt8)(DefaultSmbusRegister.Address + portOffset),
                    value  : (byte)(smbusData.Address << 1 | (smbusData.AccessMode == SmbusAccessMode.Read ? 1 : 0)));

                // Set input data for writing
                if (smbusData.AccessMode == SmbusAccessMode.Write) {
                    _ioPort.WriteByte(
                        offset : (UInt8)(DefaultSmbusRegister.Data0 + portOffset),
                        value  : smbusData.Input);
                }

                // Set offset
                _ioPort.WriteByte(
                    offset : (UInt8)(DefaultSmbusRegister.HostCmd + portOffset), 
                    value  : (byte)smbusData.Offset);

                // Command type
                byte smbusDataCmd;

                switch (smbusData.DataCommand) {
                    case SmbusDataCommand.Quick:
                        smbusDataCmd = SmbusCmd.CmdQuick;
                        break;
                    case SmbusDataCommand.Byte:
                        smbusDataCmd = SmbusCmd.CmdByte;
                        break;
                    case SmbusDataCommand.ByteData:
                    default:
                        smbusDataCmd = SmbusCmd.CmdByteData;
                        break;
                    case SmbusDataCommand.WordData:
                        smbusDataCmd = SmbusCmd.CmdWordData;
                        break;
                }

                // Execute
                _ioPort.WriteByte(
                    offset : (UInt8)(DefaultSmbusRegister.Control + portOffset), 
                    value  : (byte)(SmbusCmd.Interrupt | smbusDataCmd | SmbusCmd.Start));

                // Wait after writing
                if (smbusData.AccessMode == SmbusAccessMode.Write) {
                    Thread.Sleep(Eeprom.ValidateEepromAddress(smbusData.Address)
                        ? ExecutionDelay.WriteDelay
                        : ExecutionDelay.WaitDelay);
                }

                // Wait for completion
                if (!WaitForStatus(
                        statuses : new[] { SmbStatus.Ready, SmbStatus.Success, SmbStatus.Error }, 
                        timeout  : 1000)) {
                    
                    // Abort current execution
                    _ioPort.WriteByte((UInt8)(DefaultSmbusRegister.Control + portOffset), SmbusCmd.Stop);

                    smbusData.Status = SmbStatus.Timeout;
                    return false;
                }

                // Set status
                smbusData.Status = GetBusStatus();

                // Check status
                if (smbusData.Status == SmbStatus.Error) {
                    return false;
                }

                // Read and assign output if read mode is specified
                if (smbusData.AccessMode == SmbusAccessMode.Read) {
                    smbusData.Output = _ioPort.ReadByte(offset: DefaultSmbusRegister.Data0);
                }
            }

            return true;
        }

        /// <summary>
        /// SMBus Host Control Register data
        /// </summary>
        public struct SmbusCmd {

            /// <summary>
            /// Initiates the Smbus command 
            /// </summary>
            public const byte Start       = 1 << 6;

            /// <summary>
            /// Stops the current host transaction in process
            /// </summary>
            public const byte Stop        = 1 << 1;

            /// <summary>
            /// Enables the generation of an interrupt upon the completion of the command
            /// </summary>
            public const byte Interrupt   = 1 << 0;

            /// <summary>
            /// <b>Quick:</b>
            /// The slave address and read/write value (bit 0) are stored in the transmit slave address register.
            /// </summary>
            public const byte CmdQuick    = 0b000 << 2;

            /// <summary>
            /// <b>Byte:</b>
            /// This command uses the transmit slave address and command registers.
            /// Bit 0 of the slave address register determines if this is a read or write command.
            /// </summary>
            public const byte CmdByte     = 0b001 << 2;

            /// <summary>
            /// <b>Byte Data:</b>
            /// This command uses the transmit slave address, command, and DATA0 registers.
            /// Bit 0 of the slave address register determines if this is a read or write command.
            /// If it is a read, the DATA0 register will contain the read data.
            /// </summary>
            public const byte CmdByteData = 0b010 << 2;

            /// <summary>
            /// <b>Word Data:</b>
            /// This command uses the transmit slave address, command, DATA0 and DATA1 registers.
            /// Bit 0 of the slave address register determines if this is a read or write command.
            /// If it is a read, after the command completes, the DATA0 and DATA1 registers will contain the read data.
            /// </summary>
            public const byte CmdWordData = 0b011 << 2;
        }
        
        /// <summary>
        /// SMBus data for <see cref="ExecuteCommand"/>
        /// </summary>
        private struct SmbusData {
            internal UInt8 BusNumber;
            internal UInt8 Address;
            internal UInt16 Offset;
            internal SmbusAccessMode AccessMode;
            internal SmbusDataCommand DataCommand;
            internal byte Input;
            internal byte Output;
            internal SmbStatus Status;
        }

        /// <summary>
        /// SMBus Read/Write access
        /// </summary>
        public enum SmbusAccessMode : byte {
            Read,
            Write,
        }

        /// <summary>
        /// Smbus command type
        /// </summary>
        public enum SmbusDataCommand : byte {
            Quick,
            Byte,
            ByteData,
            WordData,
        }

        /// <summary>
        /// Command execution delays
        /// </summary>
        private struct ExecutionDelay {
            internal static readonly UInt8 WriteDelay = 10;
            internal static readonly UInt8 WaitDelay  =  0;
        }

        /// <summary>
        /// Gets SMBus status
        /// </summary>
        /// <returns>SMBus status</returns>
        public static SmbStatus GetBusStatus() {

            byte status;

            if (PlatformType == Platform.SkylakeX) {
                status = _pciDevice.ReadByte((byte)(SkylakeXSmbusRegister.Status + _busNumber * 4));

                if ((status & SkylakeXSmbusStatus.Busy) > 0) {
                    return SmbStatus.Busy;
                }

                if ((status & SkylakeXSmbusStatus.Complete) > 0) {

                    if ((status & SkylakeXSmbusStatus.Nack) > 0) {
                        return SmbStatus.Error;
                    }

                    if ((status & ~(SkylakeXSmbusStatus.Busy | SkylakeXSmbusStatus.Nack) & (SkylakeXSmbusStatus.Busy | SkylakeXSmbusStatus.Nack)) == 0) {
                        return SmbStatus.Success;
                    }
                }

                return SmbStatus.Ready;
            }

            if (_info.VendorId == VendorId.Nvidia) {
                status = _ioPort.ReadByte(NvidiaSmbusRegister.Status);

                switch (status) {
                    case NvidiaSmbusStatus.Done:
                        return SmbStatus.Success;
                    case NvidiaSmbusStatus.Error:
                    case NvidiaSmbusStatus.Invalid:
                        return SmbStatus.Error;
                }
            }

            if (PlatformType == Platform.Default) {

                status = _ioPort.ReadByte(DefaultSmbusRegister.Status);

                // Check status flags
                if ((status & (SmbusStatus.Failed |
                               SmbusStatus.BusCollision |
                               SmbusStatus.DeviceErr |
                               SmbusStatus.Interrupt |
                               SmbusStatus.HostBusy)) == 0) {
                    return SmbStatus.Ready;
                }
                                
                // Check busy flag
                if ((status & SmbusStatus.HostBusy) == SmbusStatus.HostBusy) {
                    return SmbStatus.Busy;
                }

                // Check for errors
                if ((status & (SmbusStatus.Failed |
                               SmbusStatus.BusCollision |
                               SmbusStatus.DeviceErr)) > 0) {
                    return SmbStatus.Error;
                }

                // Check Interrupt flag
                if ((status & SmbusStatus.Interrupt) == SmbusStatus.Interrupt ) {
                    return SmbStatus.Success;
                }
            }

            return SmbStatus.Error;
        }

        /// <summary>
        /// SMBus status timeout handler
        /// </summary>
        /// <param name="status">Desired SMBus status</param>
        /// <param name="timeout">Timeout in milliseconds</param>
        /// <returns><see langword="true"/> if SMBus status matches <paramref name="status"/> before timeout occurs</returns>
        private static bool WaitForStatus(SmbStatus status, int timeout) {

            Stopwatch sw = Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeout) {
                if (GetBusStatus() == status) {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// SMBus status timeout handler
        /// </summary>
        /// <param name="statuses">An array of SMBus statuses to wait for</param>
        /// <param name="timeout">Timeout in milliseconds</param>
        /// <returns><see langword="true"/> if SMBus status matches one of <paramref name="statuses"/> before timeout occurs</returns>
        private static bool WaitForStatus(SmbStatus[] statuses, int timeout) {

            SmbStatus currentStatus;
            Stopwatch sw = Stopwatch.StartNew();
            
            while (sw.ElapsedMilliseconds < timeout) {
                currentStatus = GetBusStatus();
                foreach (SmbStatus status in statuses) {
                    if (currentStatus == status) {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// SMBus status
        /// </summary>
        public enum SmbStatus : byte {
            Ready,
            Busy,
            Error,
            Success,
            Timeout,
            Aborted,
        }

        /// <summary>
        /// Host status (<see cref="DefaultSmbusRegister.Status"/>) register bits description
        /// </summary>
        public struct SmbusStatus {

            /// <summary>
            /// Indicates the SMBus controller is in the process of completing a command
            /// </summary>
            public const byte HostBusy       = 1 << 0;

            /// <summary>
            /// Indicates the successful completion of the last host command
            /// </summary>
            public const byte Interrupt      = 1 << 1;

            /// <summary>
            /// Indicates an error: illegal command, unclaimed cycle, or timeout
            /// </summary>
            public const byte DeviceErr      = 1 << 2;

            /// <summary>
            /// Indicates an SMBus transaction collision
            /// </summary>
            public const byte BusCollision   = 1 << 3;

            /// <summary>
            /// Indicates a failed bus transaction
            /// </summary>
            public const byte Failed         = 1 << 4;
        }

        /// <summary>
        /// Gets supported platform maximum SPD size
        /// </summary>
        /// <returns>SPD size</returns>
        private UInt16 GetMaxSpdSize(UInt8 address) {

            // Read dram device type byte
            byte ramTypeByte = ReadByte(this, address, 2);

            // SPD header for GetRamType
            byte[] spdHeader = { 0x00, 0x00, ramTypeByte };

            // Check if dram device type byte value is in the Ram.Type enum
            return (UInt16)(Enum.IsDefined(typeof(Spd.RamType), (Spd.RamType)ramTypeByte)
                ? Spd.GetSpdSize(Spd.GetRamType(spdHeader))
                : Spd.DataLength.MINIMUM); // DDR3 and older
        }
    }
}