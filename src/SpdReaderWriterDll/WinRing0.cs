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
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Microsoft.Win32.SafeHandles;
using SpdReaderWriterDll.Properties;
using UInt8 = System.Byte;

namespace SpdReaderWriterDll {

    /// <summary>
    /// Kernel driver (WinRing0) class
    /// </summary>
    public class WinRing0 : IDisposable {

        /// <summary>
        /// Describes driver installation state
        /// </summary>
        public bool IsInstalled => CheckDriver();

        /// <summary>
        /// Describes driver running state
        /// </summary>
        public bool IsServiceRunning {
            get {
                if (!IsInstalled) {
                    return false;
                }

                try {
                    _sc?.Refresh();
                    return _sc?.ServiceName != null && _sc.Status == ServiceControllerStatus.Running;
                }
                catch {
                    return false;
                }
            }
        }

        /// <summary>
        /// Describes driver handle open state
        /// </summary>
        public bool IsHandleOpen => _deviceHandle != null && !_deviceHandle.IsClosed;

        /// <summary>
        /// Describes driver handle valid state
        /// </summary>
        public bool IsValid => IsInstalled && _deviceHandle !=null && !_deviceHandle.IsInvalid;

        /// <summary>
        /// Describes driver ready state
        /// </summary>
        public bool IsReady => IsValid && IsHandleOpen;

        /// <summary>
        /// Initializes kernel driver
        /// </summary>
        public WinRing0() {
            if (IsInstalled && IsServiceRunning) {
                _disposeOnExit = false;
            }
            else {
                if (!(IsInstalled || InstallDriver())) {
                    throw new Exception("Unable to install driver service");
                }

                if (!(IsServiceRunning || StartDriver())) {
                    throw new Exception("Unable to start driver service");
                }
            }

            if (!(IsHandleOpen || OpenDriverHandle())) {
                throw new Exception("Unable to open driver handle");
            }
        }

        /// <summary>
        /// Kernel driver destructor
        /// </summary>
        ~WinRing0() {
            Dispose();
        }

        /// <summary>
        /// Deinitializes kernel driver instance
        /// </summary>
        public void Dispose() {
            CloseDriverHandle();

            int refCount = 0;

            DeviceIoControl(Kernel32.IoControlCode.GET_REFCOUNT, null, ref refCount);

            if (_disposeOnExit && refCount <= 1) {
                StopDriver();
                RemoveDriver(deleteFile: false);
            }

            _deviceHandle = null;

            Advapi32.CloseServiceHandle(_servicePtr);
            Advapi32.CloseServiceHandle(_managerPtr);
        }

        /// <summary>
        /// Extracts driver file from resources and saves it to a local file at <see cref="_fileName"/>
        /// </summary>
        /// <returns><see langref="true"/> if driver file is successfully extracted</returns>
        private bool ExtractDriver() {

            // Read applicable driver from resources depending on OS platform
            byte[] driverFileContents = Data.DecompressGzip(Environment.Is64BitOperatingSystem ? Resources.WinRing0x64_sys : Resources.WinRing0_sys);

            if (!(File.Exists(_fileName) && driverFileContents.SequenceEqual(File.ReadAllBytes(_fileName)))) {

                // Save driver to local file
                try {
                    File.WriteAllBytes(_fileName, driverFileContents);
                }
                catch {
                    return false;
                }
            }

            return File.Exists(_fileName) && driverFileContents.SequenceEqual(File.ReadAllBytes(_fileName));
        }

        /// <summary>
        /// Installs kernel driver
        /// </summary>
        /// <returns><see langref="true"/> if the driver is successfully installed</returns>
        private bool InstallDriver() {

            if (!ExtractDriver()) {
                return false;
            }

            if (_managerPtr == IntPtr.Zero) {
                return false;
            }

            _servicePtr = Advapi32.CreateService(
                hSCManager       : _managerPtr,
                lpServiceName    : _name,
                lpDisplayName    : _name,
                dwDesiredAccess  : Advapi32.ServiceAccessRights.SC_MANAGER_ALL_ACCESS,
                dwServiceType    : Advapi32.ServiceType.SERVICE_KERNEL_DRIVER,
                dwStartType      : Advapi32.StartType.SERVICE_DEMAND_START,
                dwErrorControl   : Advapi32.ErrorControl.SERVICE_ERROR_NORMAL,
                lpBinaryPathName : _fileName);

            if (_servicePtr == IntPtr.Zero) {
                return false;
            }

            Advapi32.CloseServiceHandle(_servicePtr);

            return true;
        }
        
        /// <summary>
        /// Deletes kernel driver
        /// </summary>
        /// <returns><see langref="true"/> if driver is successfully deleted</returns>
        private bool RemoveDriver() {

            if (_managerPtr == IntPtr.Zero) {
                return false;
            }

            _servicePtr = Advapi32.OpenService(_managerPtr, _name, Advapi32.ServiceRights.SERVICE_ALL_ACCESS);

            if (_servicePtr == IntPtr.Zero) {
                return false;
            }

            return Advapi32.DeleteService(_servicePtr) &&
                   Advapi32.CloseServiceHandle(_servicePtr);
        }

        /// <summary>
        /// Deletes kernel driver and driver file
        /// </summary>
        /// <param name="deleteFile">Set to <see langref="true"/> to delete driver file, or <see langref="false"/> to keep it</param>
        /// <returns><see langref="true"/> if the driver service and the driver file are successfully deleted</returns>
        private bool RemoveDriver(bool deleteFile) {
            if (!deleteFile) {
                return RemoveDriver();
            }

            if (!RemoveDriver()) {
                return false;
            }

            try {
                File.Delete(_fileName);
                return !File.Exists(_fileName);
            }
            catch {
                return false;
            }
        }

        /// <summary>
        /// Starts kernel driver
        /// </summary>
        /// <returns><see langref="true"/> if driver is successfully started</returns>
        private bool StartDriver() {

            if (!IsInstalled) {
                return false;
            }

            _sc = new ServiceController(_name);

            try {
                if (_sc.Status == ServiceControllerStatus.Running) {
                    return true;
                }

                _sc.Start();
                _sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromMilliseconds(_timeout));
                return _sc.Status == ServiceControllerStatus.Running;
            }
            catch {
                try {
                    return _sc.Status == ServiceControllerStatus.Running;
                }
                catch {
                    return false;
                }
            }
        }

        /// <summary>
        /// Stops kernel driver
        /// </summary>
        /// <returns><see langref="true"/> if driver is successfully stopped</returns>
        private bool StopDriver() {

            _sc = new ServiceController(_name);

            try {
                if (_sc.Status != ServiceControllerStatus.Stopped) {

                    _sc.Stop();

                    // Wait for Stopped or StopPending
                    Stopwatch sw = new Stopwatch();
                    sw.Start();

                    while (sw.ElapsedMilliseconds < _timeout) {

                        _sc.Refresh();

                        if (_sc.Status == ServiceControllerStatus.Stopped ||
                            _sc.Status == ServiceControllerStatus.StopPending) {
                            return true;
                        }
                    }
                }

                return _sc.Status == ServiceControllerStatus.Stopped || 
                       _sc.Status == ServiceControllerStatus.StopPending;
            }
            catch {
                try {
                    _sc = new ServiceController(_name);
                    return _sc.Status == ServiceControllerStatus.Stopped || 
                           _sc.Status == ServiceControllerStatus.StopPending;
                }
                catch {
                    return true;
                }
            }
        }

        /// <summary>
        /// Checks if the driver is installed
        /// </summary>
        /// <returns><see langref="true"/> if the driver is installed</returns>
        private static bool CheckDriver() {

            _sc = new ServiceController(_name);

            try {
                return _sc.ServiceType == ServiceType.KernelDriver;
            }
            catch {
                return false;
            }
        }

        /// <summary>
        /// Opens driver handle
        /// </summary>
        /// <returns><see langref="true"/> if driver handle is successfully opened</returns>
        private bool OpenDriverHandle() {

            IntPtr driverHandle = Kernel32.CreateFile(
                lpFileName            : $@"\\.\{_name}",
                dwDesiredAccess       : Kernel32.FileAccess.GENERIC_READWRITE,
                dwShareMode           : Kernel32.FileShare.FILE_SHARE_READWRITE,
                lpSecurityAttributes  : IntPtr.Zero,
                dwCreationDisposition : Kernel32.FileMode.OPEN_EXISTING,
                dwFlagsAndAttributes  : Kernel32.FileAttributes.FILE_ATTRIBUTE_NORMAL,
                hTemplateFile         : IntPtr.Zero);

            _deviceHandle = new SafeFileHandle(driverHandle, true);
            
            if (_deviceHandle.IsInvalid) {
                _deviceHandle.Close();
                _deviceHandle.Dispose();
            }

            return IsValid;
        }

        /// <summary>
        /// Closes kernel driver
        /// </summary>
        /// <returns><see langref="true"/> if driver handle is successfully closed</returns>
        private bool CloseDriverHandle() {
            if (IsHandleOpen) {
                _deviceHandle.Close();
                _deviceHandle.Dispose();
            }

            return !IsHandleOpen;
        }

        /// <summary>
        /// Retrieves driver version
        /// </summary>
        /// <param name="major">Major version number</param>
        /// <param name="minor">Minor version number</param>
        /// <param name="revision">Revision number</param>
        /// <param name="release">Release number</param>
        /// <returns>Driver version</returns>
        public UInt32 GetDriverVersion(ref byte major, ref byte minor, ref byte revision, ref byte release) {

            UInt32 output = default;

            DeviceIoControl(Kernel32.IoControlCode.GET_DRIVER_VERSION, null, ref output);

            major    = (byte)(output >> 24);
            minor    = (byte)(output >> 16);
            revision = (byte)(output >> 8);
            release  = (byte) output;

            return output;
        }

        /// <summary>
        /// Reads data from device
        /// </summary>
        /// <typeparam name="T">Output data type</typeparam>
        /// <param name="ioControlCode">IOCTL code</param>
        /// <param name="inputData">Input parameters</param>
        /// <param name="outputData">Output data returned by the driver</param>
        /// <returns><see lang="true"/> if the operation succeeds</returns>
        private static bool DeviceIoControl<T>(UInt32 ioControlCode, object inputData, ref T outputData) {

            if (!_isReady) {
                return false;
            }

            UInt32 inputSize      = (UInt32)(inputData == null ? 0 : Marshal.SizeOf(inputData));
            UInt32 returnedLength = default;
            object outputBuffer   = outputData;

            bool result = Kernel32.DeviceIoControl(
                hDevice         : _deviceHandle,
                dwIoControlCode : ioControlCode,
                lpInBuffer      : inputData,
                nInBufferSize   : inputSize,
                lpOutBuffer     : outputBuffer,
                nOutBufferSize  : (UInt32)Marshal.SizeOf(outputBuffer),
                lpBytesReturned : out returnedLength,
                lpOverlapped    : IntPtr.Zero);

            outputData = (T)outputBuffer;

            return result;
        }

        /// <summary>
        /// Writes data to device
        /// </summary>
        /// <param name="ioControlCode">IOCTL code</param>
        /// <param name="inputData">Input parameters</param>
        /// <returns><see lang="true"/> if the operation succeeds</returns>
        private static bool DeviceIoControl(UInt32 ioControlCode, object inputData) {

            if (!_isReady) {
                return false;
            }

            UInt32 inputSize      = (UInt32)(inputData == null ? 0 : Marshal.SizeOf(inputData));
            UInt32 returnedLength = default;

            return Kernel32.DeviceIoControl(
                hDevice         : _deviceHandle,
                dwIoControlCode : ioControlCode,
                lpInBuffer      : inputData,
                nInBufferSize   : inputSize,
                lpOutBuffer     : null,
                nOutBufferSize  : 0,
                lpBytesReturned : out returnedLength,
                lpOverlapped    : IntPtr.Zero);
        }

        #region PCI device

        /// <summary>
        /// Converts PCI Bus Number, Device Number, and Function Number to PCI Device Address
        /// </summary>
        /// <param name="bus">PCI Bus Number</param>
        /// <param name="dev">PCI Device Number</param>
        /// <param name="func">PCI Function Number</param>
        /// <returns>PCI Device Address</returns>
        public static UInt32 PciBusDevFunc(UInt32 bus, UInt32 dev, UInt32 func) {
            return ((bus & 0xFF) << 8) | ((dev & 0x1F) << 3) | (func & 0x07);
        }

        /// <summary>
        /// Converts PCI Device Address to PCI Bus Number
        /// </summary>
        /// <param name="address">PCI Device Address</param>
        /// <returns>PCI Bus Number</returns>
        public static UInt8 PciGetBus(UInt32 address) {
            return (UInt8)((address >> 8) & 0xFF);
        }

        /// <summary>
        /// Converts PCI Device Address to PCI Device Number
        /// </summary>
        /// <param name="address">PCI Device Address</param>
        /// <returns>PCI Device Number</returns>
        public static UInt8 PciGetDev(UInt32 address) {
            return (UInt8)((address >> 3) & 0x1F);
        }

        /// <summary>
        /// Converts PCI Device Address to PCI Function Number
        /// </summary>
        /// <param name="address">PCI Device Address</param>
        /// <returns>PCI Function Number</returns>
        public static UInt8 PciGetFunc(UInt32 address) {
            return (UInt8)(address & 0x07);
        }

        /// <summary>
        /// Sets the maximum PCI bus index to scan by <see cref="FindPciDeviceById"/> and <see cref="FindPciDeviceByClass"/>
        /// </summary>
        /// <param name="max">Maximum PCI bus index to scan</param>
        public void SetPciMaxBusIndex(UInt8 max) {
            gPciNumberOfBus = max;
        }

        /// <summary>
        /// Finds PCI device matching Vendor ID and Device ID
        /// </summary>
        /// <param name="vendorId">Vendor ID</param>
        /// <param name="deviceId">Device ID</param>
        /// <param name="index">Device index to find</param>
        /// <returns>PCI Device Address matching input <paramref name="vendorId">Vendor ID</paramref> and <paramref name="deviceId">Device ID</paramref></returns>
        public UInt32 FindPciDeviceById(UInt16 vendorId, UInt16 deviceId, UInt16 index) {

            if (index > gPciNumberOfBus * gPciNumberOfDevice * gPciNumberOfFunction) {
                throw new ArgumentOutOfRangeException();
            }

            UInt32 pciAddress = UInt32.MaxValue;
            UInt32 count      = 0;

            if (vendorId == default || deviceId == default || index == 0) {
                return pciAddress;
            }

            for (UInt16 bus = 0; bus <= gPciNumberOfBus; bus++) {
                for (UInt8 dev = 0; dev < gPciNumberOfDevice; dev++) {

                    if (ReadPciConfigWord(PciBusDevFunc(bus, dev, 0), 0x00) == UInt32.MaxValue) {
                        continue;
                    }

                    for (UInt8 func = 0; func < gPciNumberOfFunction; func++) {

                        if (ReadPciConfigDword(PciBusDevFunc(bus, dev, func), 0x00) != (UInt32)((deviceId << 16) | vendorId)) {
                            continue;
                        }

                        pciAddress = PciBusDevFunc(bus, dev, func);

                        if (++count == index) {
                            return pciAddress;
                        }
                    }
                }
            }

            return pciAddress;
        }

        /// <summary>
        /// Finds PCI devices matching Vendor ID and Device ID
        /// </summary>
        /// <param name="vendorId">Vendor ID</param>
        /// <param name="deviceId">Device ID</param>
        /// <returns>An array of PCI Device Addresses matching input <paramref name="vendorId">Vendor ID</paramref> and <paramref name="deviceId">Device ID</paramref></returns>
        public UInt32[] FindPciDeviceByIdArray(UInt16 vendorId, UInt16 deviceId) {
            return FindPciDeviceByIdArray(vendorId, deviceId, (UInt16)(gPciNumberOfBus * gPciNumberOfDevice * gPciNumberOfFunction));
        }

        /// <summary>
        /// Finds PCI devices matching Vendor ID and Device ID
        /// </summary>
        /// <param name="vendorId">Vendor ID</param>
        /// <param name="deviceId">Device ID</param>
        /// <param name="maxCount">Maximum number of devices to find</param>
        /// <returns>An array of PCI Device Addresses matching input <paramref name="vendorId">Vendor ID</paramref> and <paramref name="deviceId">Device ID</paramref></returns>
        public UInt32[] FindPciDeviceByIdArray(UInt16 vendorId, UInt16 deviceId, UInt16 maxCount) {

            if (maxCount > gPciNumberOfBus * gPciNumberOfDevice * gPciNumberOfFunction || maxCount == 0) {
                throw new ArgumentOutOfRangeException();
            }

            UInt32 count = 0;

            if (vendorId == default || deviceId == default) {
                return new UInt32[0];
            }

            Queue <UInt32> result = new Queue<UInt32>();

            for (UInt16 bus = 0; bus <= gPciNumberOfBus; bus++) {

                if (ReadPciConfigWord(PciBusDevFunc(bus, 0x00, 0x00), 0x00) != vendorId) {
                    continue;
                }

                for (UInt8 dev = 0; dev < gPciNumberOfDevice; dev++) {

                    if (ReadPciConfigWord(PciBusDevFunc(bus, dev, 0), 0x00) == UInt16.MaxValue) {
                        continue;
                    }

                    for (UInt8 func = 0; func < gPciNumberOfFunction; func++) {

                        if (ReadPciConfigDword(PciBusDevFunc(bus, dev, func), 0x00) !=
                            (UInt32)(vendorId | (deviceId << 16))) {
                            continue;
                        }

                        result.Enqueue(PciBusDevFunc(bus, dev, func));

                        if (++count == maxCount) {
                            return result.ToArray();
                        }
                    }
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// Finds PCI device by Device Class
        /// </summary>
        /// <param name="baseClass">Base Class</param>
        /// <param name="subClass">Sub Class</param>
        /// <param name="programIf">Program Interface</param>
        /// <param name="index">Device index to find</param>
        /// <returns>PCI Device Address matching input <paramref name="baseClass"/>, <paramref name="subClass"/>, and <paramref name="programIf"/></returns>
        public UInt32 FindPciDeviceByClass(UInt8 baseClass, UInt8 subClass, UInt8 programIf, UInt16 index) {

            if (index > gPciNumberOfBus * gPciNumberOfDevice * gPciNumberOfFunction) {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            UInt32 pciAddress = UInt32.MaxValue;
            UInt32 count      = 0;

            for (UInt16 bus = 0; bus <= gPciNumberOfBus; bus++) {
                for (UInt8 dev = 0; dev < gPciNumberOfDevice; dev++) {

                    if (ReadPciConfigWord(PciBusDevFunc(bus, dev, 0), 0x00) == UInt16.MaxValue) {
                        continue;
                    }

                    for (UInt8 func = 0; func < gPciNumberOfFunction; func++) {

                        if ((ReadPciConfigDword(PciBusDevFunc(bus, dev, func), 0x08) & 0xFFFFFF00) !=
                            (UInt32)(baseClass << 24 | subClass << 16 | programIf << 8)) {
                            continue;
                        }

                        pciAddress = PciBusDevFunc(bus, dev, func);

                        if (++count == index) {
                            return pciAddress;
                        }
                    }
                }
            }

            return pciAddress;
        }

        /// <summary>
        /// Finds PCI devices by Device Class
        /// </summary>
        /// <param name="baseClass">Base Class</param>
        /// <param name="subClass">Sub Class</param>
        /// <param name="programIf">Program Interface</param>
        /// <returns>An array of PCI Device Address matching input <paramref name="baseClass"/>, <paramref name="subClass"/>, and <paramref name="programIf"/></returns>
        public UInt32[] FindPciDeviceByClassArray(UInt8 baseClass, UInt8 subClass, UInt8 programIf) {
            return FindPciDeviceByClassArray(baseClass, subClass, programIf, (UInt16)(gPciNumberOfBus * gPciNumberOfDevice * gPciNumberOfFunction));
        }

        /// <summary>
        /// Finds PCI devices by Device Class
        /// </summary>
        /// <param name="baseClass">Base Class</param>
        /// <param name="subClass">Sub Class</param>
        /// <param name="programIf">Program Interface</param>
        /// <param name="maxCount">Maximum number of devices to find</param>
        /// <returns>An array of PCI Device Address matching input <paramref name="baseClass"/>, <paramref name="subClass"/>, and <paramref name="programIf"/></returns>
        public UInt32[] FindPciDeviceByClassArray(UInt8 baseClass, UInt8 subClass, UInt8 programIf, UInt16 maxCount) {

            if (maxCount > gPciNumberOfBus * gPciNumberOfDevice * gPciNumberOfFunction) {
                throw new ArgumentOutOfRangeException(nameof(maxCount));
            }

            if (maxCount == 0) {
                return new UInt32[0];
            }

            UInt32 count = 0;

            Queue<UInt32> result = new Queue<UInt32>();

            for (UInt16 bus = 0; bus <= gPciNumberOfBus; bus++) {
                for (UInt8 dev = 0; dev < gPciNumberOfDevice; dev++) {

                    if (ReadPciConfigWord(PciBusDevFunc(bus, dev, 0), 0x00) == UInt16.MaxValue) {
                        continue;
                    }

                    for (UInt8 func = 0; func < gPciNumberOfFunction; func++) {

                        if ((ReadPciConfigDword(PciBusDevFunc(bus, dev, func), 0x08) & 0xFFFFFF00) !=
                            (UInt32)(baseClass << 24 | subClass << 16 | programIf << 8)) {
                            continue;
                        }

                        result.Enqueue(PciBusDevFunc(bus, dev, func));

                        if (++count == maxCount) {
                            return result.ToArray();
                        }
                    }
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// Reads a byte value from the specified PCI configuration address
        /// </summary>
        /// <param name="pciAddress">PCI device address</param>
        /// <param name="regAddress">Configuration register address</param>
        /// <returns>Returns a byte value read from the specified PCI configuration address</returns>
        public byte ReadPciConfigByte(UInt32 pciAddress, UInt32 regAddress) {

            ReadPciConfigByteEx(pciAddress, regAddress, out byte output);

            return output;
        }

        /// <summary>
        /// Reads a byte value from the specified PCI configuration address
        /// </summary>
        /// <param name="pciAddress">PCI device address</param>
        /// <param name="regAddress">Configuration register address</param>
        /// <param name="output">Byte value read from the specified PCI configuration address</param>
        /// <returns><see lang="true"/> if the function succeeds</returns>
        public bool ReadPciConfigByteEx(UInt32 pciAddress, UInt32 regAddress, out byte output) {

            output = UInt8.MaxValue;
            
            ReadPciConfigInput pciData = new ReadPciConfigInput {
                PciAddress = pciAddress,
                RegAddress = regAddress,
            };

            return DeviceIoControl(Kernel32.IoControlCode.READ_PCI_CONFIG, pciData, ref output);
        }

        /// <summary>
        /// Reads a UInt16 value from the specified PCI configuration address
        /// </summary>
        /// <param name="pciAddress">PCI device address</param>
        /// <param name="regAddress">Configuration register address</param>
        /// <returns>Returns a UInt16 value read from the specified PCI configuration address.</returns>
        public UInt16 ReadPciConfigWord(UInt32 pciAddress, UInt32 regAddress) {

            ReadPciConfigWordEx(pciAddress, regAddress, out UInt16 output);

            return output;
        }

        /// <summary>
        /// Reads a UInt16 value from the specified PCI configuration address
        /// </summary>
        /// <param name="pciAddress">PCI device address</param>
        /// <param name="regAddress">Configuration register address</param>
        /// <param name="output">UInt16 value read from the specified PCI configuration address.</param>
        /// <returns><see lang="true"/> if the function succeeds</returns>
        public bool ReadPciConfigWordEx(UInt32 pciAddress, UInt32 regAddress, out UInt16 output) {

            output = UInt16.MaxValue;
            
            ReadPciConfigInput pciData = new ReadPciConfigInput {
                PciAddress = pciAddress,
                RegAddress = regAddress
            };

            return DeviceIoControl(Kernel32.IoControlCode.READ_PCI_CONFIG, pciData, ref output);
        }

        /// <summary>
        /// Reads a UInt32 value from the specified PCI configuration address
        /// </summary>
        /// <param name="pciAddress">PCI device address</param>
        /// <param name="regAddress">Configuration register address</param>
        /// <returns>Returns a UInt32 value read from the specified PCI configuration address.</returns>
        public UInt32 ReadPciConfigDword(UInt32 pciAddress, UInt32 regAddress) {

            ReadPciConfigDwordEx(pciAddress, regAddress, out UInt32 output);

            return output;
        }

        /// <summary>
        /// Reads a UInt32 value from the specified PCI configuration address
        /// </summary>
        /// <param name="pciAddress">PCI device address</param>
        /// <param name="regAddress">Configuration register address</param>
        /// <param name="output">UInt32 value read from the specified PCI configuration address.</param>
        /// <returns><see lang="true"/> if the function succeeds</returns>
        public bool ReadPciConfigDwordEx(UInt32 pciAddress, UInt32 regAddress, out UInt32 output) {

            output = UInt32.MaxValue;
            
            ReadPciConfigInput pciData = new ReadPciConfigInput {
                PciAddress = pciAddress,
                RegAddress = regAddress
            };

            return DeviceIoControl(Kernel32.IoControlCode.READ_PCI_CONFIG, pciData, ref output);
        }

        /// <summary>
        /// Writes a byte value to the specified PCI configuration address
        /// </summary>
        /// <param name="pciAddress">PCI device address</param>
        /// <param name="regAddress">Configuration register address</param>
        /// <param name="value">Byte value to write to the configuration register</param>
        public void WritePciConfigByte(UInt32 pciAddress, UInt32 regAddress, byte value) {
            WritePciConfigByteEx(pciAddress, regAddress, value);
        }

        /// <summary>
        /// Writes a byte value to the specified PCI configuration address
        /// </summary>
        /// <param name="pciAddress">PCI device address</param>
        /// <param name="regAddress">Configuration register address</param>
        /// <param name="value">Byte value to write to the configuration register</param>
        /// <returns><see lang="true"/> if the function succeeds</returns>
        public bool WritePciConfigByteEx(UInt32 pciAddress, UInt32 regAddress, byte value) {

            WritePciConfigInputByte pciData = new WritePciConfigInputByte {
                PciAddress = pciAddress,
                RegAddress = regAddress,
                Value      = value
            };

            return DeviceIoControl(Kernel32.IoControlCode.WRITE_PCI_CONFIG, pciData);
        }

        /// <summary>
        /// Writes a UInt16 value to the specified PCI configuration address
        /// </summary>
        /// <param name="pciAddress">PCI device address</param>
        /// <param name="regAddress">Configuration register address</param>
        /// <param name="value">UInt16 value to write to the configuration register</param>
        public void WritePciConfigWord(UInt32 pciAddress, UInt32 regAddress, UInt16 value) {
            WritePciConfigWordEx(pciAddress, regAddress, value);
        }

        /// <summary>
        /// Writes a UInt16 value to the specified PCI configuration address
        /// </summary>
        /// <param name="pciAddress">PCI device address</param>
        /// <param name="regAddress">Configuration register address</param>
        /// <param name="value">Word value to write to the configuration register</param>
        /// <returns><see lang="true"/> if the function succeeds</returns>
        public bool WritePciConfigWordEx(UInt32 pciAddress, UInt32 regAddress, UInt16 value) {

            // Check UInt16 boundary alignment
            if ((regAddress & 1) != 0) {
                return false;
            }

            WritePciConfigInputWord pciData = new WritePciConfigInputWord {
                PciAddress = pciAddress,
                RegAddress = regAddress,
                Value      = value
            };

            return DeviceIoControl(Kernel32.IoControlCode.WRITE_PCI_CONFIG, pciData);
        }

        /// <summary>
        /// Writes a UInt32 value to the specified PCI configuration address
        /// </summary>
        /// <param name="pciAddress">PCI device address</param>
        /// <param name="regAddress">Configuration register address</param>
        /// <param name="value">UInt32 value to write to the configuration register</param>
        public void WritePciConfigDword(UInt32 pciAddress, UInt32 regAddress, UInt32 value) {
            WritePciConfigDwordEx(pciAddress, regAddress, value);
        }

        /// <summary>
        /// Writes a UInt32 value to the specified PCI configuration address
        /// </summary>
        /// <param name="pciAddress">PCI device address</param>
        /// <param name="regAddress">Configuration register address</param>
        /// <param name="value">UInt32 value to write to the configuration register</param>
        /// <returns><see lang="true"/> if the function succeeds</returns>
        public bool WritePciConfigDwordEx(UInt32 pciAddress, UInt32 regAddress, UInt32 value) {

            // Check UInt32 boundary alignment
            if ((regAddress & 3) != 0) {
                return false;
            }

            WritePciConfigInputDword pciData = new WritePciConfigInputDword {
                PciAddress = pciAddress,
                RegAddress = regAddress,
                Value      = value
            };

            return DeviceIoControl(Kernel32.IoControlCode.WRITE_PCI_CONFIG, pciData);
        }

        /// <summary>
        /// PCI address and register offset used by <see cref="DeviceIoControl"/> for reading from PCI device
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct ReadPciConfigInput {
            public UInt32 PciAddress;
            public UInt32 RegAddress;
        }

        /// <summary>
        /// PCI address, register offset, and byte value used by <see cref="DeviceIoControl"/> for writing bytes to PCI device
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WritePciConfigInputByte {
            public UInt32 PciAddress;
            public UInt32 RegAddress;
            public byte   Value;
        }

        /// <summary>
        /// PCI address, register offset, and word value used by <see cref="DeviceIoControl"/> for writing words to PCI device
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WritePciConfigInputWord {
            public UInt32 PciAddress;
            public UInt32 RegAddress;
            public UInt16 Value;
        }

        /// <summary>
        /// PCI address, register offset, and dword value used by <see cref="DeviceIoControl"/> for writing dwords to PCI device
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WritePciConfigInputDword {
            public UInt32 PciAddress;
            public UInt32 RegAddress;
            public UInt32 Value;
        }

        /// <summary>
        /// Maximum number of PCI buses assigned by <see cref="SetPciMaxBusIndex"/>
        /// </summary>
        private UInt8 gPciNumberOfBus            = 255;

        /// <summary>
        /// Maximum number of PCI devices per bus
        /// </summary>
        private const UInt8 gPciNumberOfDevice   = 32;

        /// <summary>
        /// Maximum number of PCI functions per device
        /// </summary>
        private const UInt8 gPciNumberOfFunction = 8;

        #endregion

        #region IO Port

        /// <summary>
        /// Reads a byte value from the specified I/O port address
        /// </summary>
        /// <param name="port">I/O port address</param>
        /// <returns>Byte value read from the specified <paramref name="port">I/O port address</paramref></returns>
        public byte ReadIoPortByte(UInt16 port) {

            ReadIoPortByteEx(port, out byte output);

            return output;
        }

        /// <summary>
        /// Reads a byte value from the specified I/O port address
        /// </summary>
        /// <param name="port">I/O port address</param>
        /// <param name="output">Byte value read from the specified <paramref name="port">I/O port address</paramref></param>
        /// <returns><see lang="true"/> if the function succeeds</returns>
        public bool ReadIoPortByteEx(UInt16 port, out byte output) {

            output = UInt8.MaxValue;

            ReadIoPortInput portData = new ReadIoPortInput {
                PortNumber = port
            };

            return DeviceIoControl(Kernel32.IoControlCode.READ_IO_PORT_BYTE, portData, ref output);
        }

        /// <summary>
        /// Reads a UInt16 value from the specified I/O port address
        /// </summary>
        /// <param name="port">I/O port address</param>
        /// <returns>UInt16 value read from the specified <paramref name="port">I/O port address</paramref></returns>
        public UInt16 ReadIoPortWord(UInt16 port) {

            ReadIoPortWordEx(port, out UInt16 output);

            return output;
        }

        /// <summary>
        /// Reads a UInt16 value from the specified I/O port address
        /// </summary>
        /// <param name="port">I/O port address</param>
        /// <param name="output">UInt16 value read from the specified <paramref name="port">I/O port address</paramref></param>
        /// <returns><see lang="true"/> if the function succeeds</returns>
        public bool ReadIoPortWordEx(UInt16 port, out UInt16 output) {

            output = UInt16.MaxValue;

            ReadIoPortInput portData = new ReadIoPortInput {
                PortNumber = port
            };

            return DeviceIoControl(Kernel32.IoControlCode.READ_IO_PORT_WORD, portData, ref output);
        }

        /// <summary>
        /// Reads a UInt32 value from the specified I/O port address
        /// </summary>
        /// <param name="port">I/O port address</param>
        /// <returns>UInt32 value read from the specified <paramref name="port">I/O port address</paramref></returns>
        public UInt32 ReadIoPortDword(UInt16 port) {

            ReadIoPortDwordEx(port, out UInt32 output);

            return output;
        }

        /// <summary>
        /// Reads a UInt32 value from the specified I/O port address
        /// </summary>
        /// <param name="port">I/O port address</param>
        /// <param name="output">UInt32 value read from the specified <paramref name="port">I/O port address</paramref></param>
        /// <returns><see lang="true"/> if the function succeeds</returns>
        public bool ReadIoPortDwordEx(UInt16 port, out UInt32 output) {

            output = UInt32.MaxValue;

            ReadIoPortInput portData = new ReadIoPortInput {
                PortNumber = port
            };
            
            return DeviceIoControl(Kernel32.IoControlCode.READ_IO_PORT_DWORD, portData, ref output);
        }

        /// <summary>
        /// Writes a byte value to the specified I/O port address
        /// </summary>
        /// <param name="port">I/O port address</param>
        /// <param name="value">Byte value to write to the port</param>
        public void WriteIoPortByte(UInt16 port, byte value) {
            WriteIoPortByteEx(port, value);
        }

        /// <summary>
        /// Writes a byte value to the specified I/O port address
        /// </summary>
        /// <param name="port">I/O port address</param>
        /// <param name="value">Byte value to write to the port</param>
        /// <returns><see lang="true"/> if the function succeeds</returns>
        public bool WriteIoPortByteEx(UInt16 port, byte value) {

            WriteIoPortInput portData = new WriteIoPortInput {
                PortNumber = port,
                Value      = value
            };

            return DeviceIoControl(Kernel32.IoControlCode.WRITE_IO_PORT_BYTE, portData);
        }

        /// <summary>
        /// Writes a UInt16 value to the specified I/O port address
        /// </summary>
        /// <param name="port">I/O port address</param>
        /// <param name="value">UInt16 value to write to the port</param>
        public void WriteIoPortWord(UInt16 port, UInt16 value) {
            WriteIoPortWordEx(port, value);
        }

        /// <summary>
        /// Writes a UInt16 value to the specified I/O port address
        /// </summary>
        /// <param name="port">I/O port address</param>
        /// <param name="value">UInt16 value to write to the port</param>
        /// <returns><see lang="true"/> if the function succeeds</returns>
        public bool WriteIoPortWordEx(UInt16 port, UInt16 value) {

            WriteIoPortInput portData = new WriteIoPortInput {
                PortNumber = port,
                Value      = value
            };

            return DeviceIoControl(Kernel32.IoControlCode.WRITE_IO_PORT_WORD, portData);
        }

        /// <summary>
        /// Writes a UInt32 value to the specified I/O port address
        /// </summary>
        /// <param name="port">I/O port address</param>
        /// <param name="value">UInt32 value to write to the port</param>
        public void WriteIoPortDword(UInt16 port, UInt32 value) {
            WriteIoPortDwordEx(port, value);
        }

        /// <summary>
        /// Writes a UInt32 value to the specified I/O port address
        /// </summary>
        /// <param name="port">I/O port address</param>
        /// <param name="value">UInt32 value to write to the port</param>
        /// <returns><see lang="true"/> if the function succeeds</returns>
        public bool WriteIoPortDwordEx(UInt16 port, UInt32 value) {

            WriteIoPortInput portData = new WriteIoPortInput {
                PortNumber = port,
                Value      = value
            };

            return DeviceIoControl(Kernel32.IoControlCode.WRITE_IO_PORT_DWORD, portData);
        }

        /// <summary>
        /// IO Port address used by <see cref="DeviceIoControl"/> for reading from an I/O port
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct ReadIoPortInput {
            public UInt32 PortNumber;
        }

        /// <summary>
        /// IO Port address and value used by <see cref="DeviceIoControl"/> for writing to an I/O port
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WriteIoPortInput {
            public UInt32 PortNumber;
            public UInt32 Value;
        }

        #endregion

        /// <summary>
        /// Driver and service name
        /// </summary>
        private static readonly string _name = "WinRing0_1_2_0"; // WinRing0_1_2_0

        /// <summary>
        /// Service operation timeout
        /// </summary>
        private static readonly Int32 _timeout = 1000;

        /// <summary>
        /// IO device handle
        /// </summary>
        private static SafeFileHandle _deviceHandle;

        /// <summary>
        /// Service controller for the driver
        /// </summary>
        private static ServiceController _sc;

        /// <summary>
        /// Driver ready state
        /// </summary>
        private static bool _isReady => _deviceHandle != null && !_deviceHandle.IsInvalid && !_deviceHandle.IsClosed;

        /// <summary>
        /// Service control manager pointer
        /// </summary>
        private IntPtr _managerPtr = Advapi32.OpenSCManager(dwAccess: Advapi32.ServiceAccessRights.SC_MANAGER_ALL_ACCESS);

        /// <summary>
        /// Service object pointer
        /// </summary>
        private IntPtr _servicePtr;

        /// <summary>
        /// Path to driver file
        /// </summary>
        private string _fileName => Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "\\" +
                                    Path.ChangeExtension(Path.GetFileName(Assembly.GetExecutingAssembly().Location), "sys");
        
        /// <summary>
        /// Indicates whether the driver service should be stopped and deleted on exit
        /// </summary>
        private bool _disposeOnExit = true;

        /// <summary>
        /// Windows NT Kernel BASE API
        /// </summary>
        private class Kernel32 {

            /// <summary>
            /// Creates or opens a file or I/O device.
            /// </summary>
            /// <param name="lpFileName">The name of the file or device to be created or opened.</param>
            /// <param name="dwDesiredAccess">The requested access to the file or device, which can be of <see cref="FileAccess"/> values.</param>
            /// <param name="dwShareMode">The requested sharing mode of the file or device, which can be of <see cref="FileShare"/> values.</param>
            /// <param name="lpSecurityAttributes">A pointer to optional SECURITY_ATTRIBUTES structure or IntPtr.Zero</param>
            /// <param name="dwCreationDisposition">An action to take on a file or device that exists or does not exist. For devices other than files, this parameter is usually set to <see cref="FileMode.OPEN_EXISTING"/>.</param>
            /// <param name="dwFlagsAndAttributes">The file or device attributes and flags.</param>
            /// <param name="hTemplateFile">A valid handle to a template file with the <see cref="FileAccess.GENERIC_READ"/> access right.</param>
            /// <returns>If the function succeeds, the return value is an open handle to the specified file, device, named pipe, or mail slot.</returns>
            [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
            internal static extern IntPtr CreateFile(
                [MarshalAs(UnmanagedType.LPTStr)] string lpFileName,
                [MarshalAs(UnmanagedType.U4)] FileAccess dwDesiredAccess,
                [MarshalAs(UnmanagedType.U4)] FileShare dwShareMode,
                [Optional] IntPtr lpSecurityAttributes,
                [MarshalAs(UnmanagedType.U4)] FileMode dwCreationDisposition,
                [MarshalAs(UnmanagedType.U4)] FileAttributes dwFlagsAndAttributes,
                IntPtr hTemplateFile);

            /// <summary>
            /// The requested access to the file or device used by <see cref="CreateFile"/>
            /// </summary>
            internal enum FileAccess {
                GENERIC_NEITHER              = 0,
                GENERIC_ALL                  = 1 << 28,
                GENERIC_EXECUTE              = 1 << 29,
                GENERIC_WRITE                = 1 << 30,
                GENERIC_READ                 = 1 << 31,
                GENERIC_READWRITE            = GENERIC_WRITE | GENERIC_READ,
            }

            /// <summary>
            /// The requested sharing mode of the file or device
            /// </summary>
            internal enum FileShare {

                /// <summary>
                /// Prevents other processes from opening a file or device if they request delete, read, or write access.
                /// </summary>
                FILE_SHARE_EXCLUSIVE         = 0x00000000,

                /// <summary>
                /// Enables subsequent open operations on a file or device to request delete access.
                /// </summary>
                FILE_SHARE_DELETE            = 0x00000004,

                /// <summary>
                /// Enables subsequent open operations on a file or device to request read access.
                /// </summary>
                FILE_SHARE_READ              = 0x00000001,

                /// <summary>
                /// Enables subsequent open operations on a file or device to request write access.
                /// </summary>
                FILE_SHARE_WRITE             = 0x00000002,

                /// <summary>
                /// Enables subsequent open operations on a file or device to request read and write access.
                /// </summary>
                FILE_SHARE_READWRITE = FILE_SHARE_READ | FILE_SHARE_WRITE,
            }

            /// <summary>
            /// An action to take on a file or device that exists or does not exist.
            /// </summary>
            internal enum FileMode {

                /// <summary>
                /// Creates a new file, only if it does not already exist.
                /// </summary>
                CREATE_NEW                   = 1,

                /// <summary>
                /// Creates a new file, always. 
                /// </summary>
                CREATE_ALWAYS                = 2,

                /// <summary>
                /// Opens a file or device, only if it exists. 
                /// </summary>
                OPEN_EXISTING                = 3,

                /// <summary>
                /// Opens a file, always. 
                /// </summary>
                OPEN_ALWAYS                  = 4,

                /// <summary>
                /// Opens a file and truncates it so that its size is zero bytes, only if it exists. 
                /// </summary>
                TRUNCATE_EXISTING            = 5,
            }

            /// <summary>
            /// File or device attributes and flags.
            /// This parameter can include any combination of the available file attributes or <see cref="FILE_ATTRIBUTE_NORMAL"/>
            /// All other file attributes override <see cref="FILE_ATTRIBUTE_NORMAL"/>.
            /// </summary>
            internal enum FileAttributes : UInt32 {

                /// <summary>
                /// The file is read only. Applications can read the file, but cannot write to or delete it.
                /// </summary>
                FILE_ATTRIBUTE_READONLY      = 0x1,

                /// <summary>
                /// The file is hidden. Do not include it in an ordinary directory listing. 
                /// </summary>
                FILE_ATTRIBUTE_HIDDEN        = 0x2,

                /// <summary>
                /// The file is part of or used exclusively by an operating system. 
                /// </summary>
                FILE_ATTRIBUTE_SYSTEM        = 0x4,

                /// <summary>
                /// The file should be archived. Applications use this attribute to mark files for backup or removal. 
                /// </summary>
                FILE_ATTRIBUTE_ARCHIVE       = 0x20,

                /// <summary>
                /// The file does not have other attributes set. This attribute is valid only if used alone. 
                /// </summary>
                FILE_ATTRIBUTE_NORMAL        = 0x80,

                /// <summary>
                /// The file is being used for temporary storage. 
                /// </summary>
                FILE_ATTRIBUTE_TEMPORARY     = 0x100,

                /// <summary>
                /// The data of a file is not immediately available. This attribute indicates that file data is physically moved to offline storage.
                /// </summary>
                FILE_ATTRIBUTE_OFFLINE       = 0x1000,

                /// <summary>
                /// The file or directory is encrypted. For a file, data is encrypted. For a directory, encryption is enabled for newly created files and subdirectories. 
                /// </summary>
                FILE_ATTRIBUTE_ENCRYPTED     = 0x4000,

                /// <summary>
                /// The file is being opened or created for a backup or restore operation. 
                /// </summary>
                FILE_FLAG_BACKUP_SEMANTICS   = 0x02000000,

                /// <summary>
                /// The file is to be deleted immediately after all of its handles are closed, which includes the specified handle and any other open or duplicated handles. 
                /// </summary>
                FILE_FLAG_DELETE_ON_CLOSE    = 0x04000000,

                /// <summary>
                /// The file or device is being opened with no system caching for data reads and writes. 
                /// </summary>
                FILE_FLAG_NO_BUFFERING       = 0x20000000,

                /// <summary>
                /// The file data is requested, but it should continue to be located in remote storage.
                /// </summary>
                FILE_FLAG_OPEN_NO_RECALL     = 0x00100000,

                /// <summary>
                /// Normal reparse point processing will not occur; <see cref="CreateFile"/> will attempt to open the reparse point.
                /// When a file is opened, a file handle is returned, whether or not the filter that controls the reparse point is operational.
                /// This flag cannot be used with the <see cref="FileMode.CREATE_ALWAYS"/> flag.
                /// </summary>
                FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000,

                /// <summary>
                /// The file or device is being opened or created for asynchronous I/O. 
                /// </summary>
                FILE_FLAG_OVERLAPPED         = 0x40000000,
                
                /// <summary>
                /// Access will occur according to POSIX rules. This includes allowing multiple files with names, differing only in case, for file systems that support that naming. 
                /// </summary>
                FILE_FLAG_POSIX_SEMANTICS    = 0x01000000,

                /// <summary>
                /// Access is intended to be random. The system can use this as a hint to optimize file caching. 
                /// </summary>
                FILE_FLAG_RANDOM_ACCESS      = 0x10000000,

                /// <summary>
                /// The file or device is being opened with session awareness. 
                /// </summary>
                FILE_FLAG_SESSION_AWARE      = 0x00800000,

                /// <summary>
                /// Access is intended to be sequential from beginning to end.
                /// </summary>
                FILE_FLAG_SEQUENTIAL_SCAN    = 0x08000000,

                /// <summary>
                /// Write operations will not go through any intermediate cache, they will go directly to disk.
                /// </summary>
                FILE_FLAG_WRITE_THROUGH      = 0x80000000,
            }

            /// <summary>
            /// Sends a control code directly to a specified device driver, causing the corresponding device to perform the corresponding operation.
            /// </summary>
            /// <param name="hDevice">A handle to the device on which the operation is to be performed.</param>
            /// <param name="dwIoControlCode">The control code for the operation. This value identifies the specific operation to be performed and the type of device on which to perform it.</param>
            /// <param name="lpInBuffer">A pointer to the input buffer that contains the data required to perform the operation.</param>
            /// <param name="nInBufferSize">The size of the input buffer, in bytes.</param>
            /// <param name="lpOutBuffer">A pointer to the output buffer that is to receive the data returned by the operation.</param>
            /// <param name="nOutBufferSize">The size of the output buffer, in bytes.</param>
            /// <param name="lpBytesReturned">A pointer to a variable that receives the size of the data stored in the output buffer, in bytes.</param>
            /// <param name="lpOverlapped">A pointer to an OVERLAPPED structure.</param>
            /// <returns>If the operation completes successfully, the return value is nonzero (<see lang="true"/>). If the operation fails or is pending, the return value is zero.</returns>
            [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Auto)]
            internal static extern bool DeviceIoControl(
                SafeFileHandle hDevice,
                uint dwIoControlCode,
                [MarshalAs(UnmanagedType.AsAny)][In] object lpInBuffer,
                uint nInBufferSize,
                [MarshalAs(UnmanagedType.AsAny)][Out] object lpOutBuffer,
                uint nOutBufferSize,
                out uint lpBytesReturned,
                IntPtr lpOverlapped);

            /// <summary>
            /// WinRing0 IO control codes
            /// </summary>
            public struct IoControlCode {
                /// <summary>
                /// Winring0 Device type code
                /// </summary>
                public static readonly UInt32 DEVICE_TYPE = 0x9C40;     // 40000

                public static UInt32 GET_DRIVER_VERSION   = 0x9C402000; // CTL_CODE(function: 0x800, access: IOCTL_ACCESS.FILE_ANY_ACCESS);
                public static UInt32 GET_REFCOUNT         = 0x9C402004; // CTL_CODE(function: 0x801, access: IOCTL_ACCESS.FILE_ANY_ACCESS);
                public static UInt32 READ_MSR             = 0x9C402084; // CTL_CODE(function: 0x821, access: IOCTL_ACCESS.FILE_ANY_ACCESS);
                public static UInt32 WRITE_MSR            = 0x9C402088; // CTL_CODE(function: 0x822, access: IOCTL_ACCESS.FILE_ANY_ACCESS);
                public static UInt32 READ_PMC             = 0x9C40208C; // CTL_CODE(function: 0x823, access: IOCTL_ACCESS.FILE_ANY_ACCESS);
                public static UInt32 HALT                 = 0x9C402090; // CTL_CODE(function: 0x824, access: IOCTL_ACCESS.FILE_ANY_ACCESS);
                public static UInt32 READ_IO_PORT         = 0x9C4060C4; // CTL_CODE(function: 0x831, access: IOCTL_ACCESS.FILE_READ_DATA); 
                public static UInt32 WRITE_IO_PORT        = 0x9C40A0C8; // CTL_CODE(function: 0x832, access: IOCTL_ACCESS.FILE_WRITE_DATA);
                public static UInt32 READ_IO_PORT_BYTE    = 0x9C4060CC; // CTL_CODE(function: 0x833, access: IOCTL_ACCESS.FILE_READ_DATA); 
                public static UInt32 READ_IO_PORT_WORD    = 0x9C4060D0; // CTL_CODE(function: 0x834, access: IOCTL_ACCESS.FILE_READ_DATA); 
                public static UInt32 READ_IO_PORT_DWORD   = 0x9C4060D4; // CTL_CODE(function: 0x835, access: IOCTL_ACCESS.FILE_READ_DATA); 
                public static UInt32 WRITE_IO_PORT_BYTE   = 0x9C40A0D8; // CTL_CODE(function: 0x836, access: IOCTL_ACCESS.FILE_WRITE_DATA);
                public static UInt32 WRITE_IO_PORT_WORD   = 0x9C40A0DC; // CTL_CODE(function: 0x837, access: IOCTL_ACCESS.FILE_WRITE_DATA);
                public static UInt32 WRITE_IO_PORT_DWORD  = 0x9C40A0E0; // CTL_CODE(function: 0x838, access: IOCTL_ACCESS.FILE_WRITE_DATA);
                public static UInt32 READ_MEMORY          = 0x9C406104; // CTL_CODE(function: 0x841, access: IOCTL_ACCESS.FILE_READ_DATA); 
                public static UInt32 WRITE_MEMORY         = 0x9C40A108; // CTL_CODE(function: 0x842, access: IOCTL_ACCESS.FILE_WRITE_DATA);
                public static UInt32 READ_PCI_CONFIG      = 0x9C406144; // CTL_CODE(function: 0x851, access: IOCTL_ACCESS.FILE_READ_DATA); 
                public static UInt32 WRITE_PCI_CONFIG     = 0x9C40A148; // CTL_CODE(function: 0x852, access: IOCTL_ACCESS.FILE_WRITE_DATA);
            }

            /// <summary>
            /// Defines a new IO Control Code based on function and desired access parameters only
            /// </summary>
            /// <param name="function">Identifies the function to be performed by the driver.</param>
            /// <param name="access">Indicates the type of access that a caller must request when opening the file object that represents the device.</param>
            /// <returns>An I/O control code</returns>
            internal static UInt32 CTL_CODE(uint function, IOCTL_ACCESS access) {
                return CTL_CODE(IoControlCode.DEVICE_TYPE, function, IOCTL_METHOD.METHOD_BUFFERED, access);
            }

            /// <summary>
            /// Defines a new IO Control Code
            /// </summary>
            /// <param name="deviceType">Identifies the device type.</param>
            /// <param name="function">Identifies the function to be performed by the driver.</param>
            /// <param name="method">Indicates how the system will pass data between the caller of <see cref="DeviceIoControl"/> and the driver that handles the IRP. Use one of the <see cref="IOCTL_METHOD"/> constants.</param>
            /// <param name="access">Indicates the type of access that a caller must request when opening the file object that represents the device.</param>
            /// <returns>An I/O control code</returns>
            internal static UInt32 CTL_CODE(uint deviceType, uint function, IOCTL_METHOD method, IOCTL_ACCESS access) {
                return (deviceType << 16) | ((uint)access << 14) | (uint)((UInt16)(function) << 2) | (uint)method;
            }
            
            /// <summary>
            /// Indicates how the system will pass data between the caller of <see cref="DeviceIoControl"/> and the driver that handles the IRP.
            /// </summary>
            internal enum IOCTL_METHOD : uint {

                /// <summary>
                /// Specifies the buffered I/O method, which is typically used for transferring small amounts of data per request. 
                /// </summary>
                METHOD_BUFFERED   = 0,

                /// <summary>
                /// Specifies the direct I/O method, which is typically used for writing large amounts of data, using DMA or PIO, that must be transferred quickly.
                /// </summary>
                METHOD_IN_DIRECT  = 1,

                /// <summary>
                /// Specifies the direct I/O method, which is typically used for reading large amounts of data, using DMA or PIO, that must be transferred quickly.
                /// </summary>
                METHOD_OUT_DIRECT = 2,

                /// <summary>
                /// Specifies neither buffered nor direct I/O. The I/O manager does not provide any system buffers or MDLs.
                /// </summary>
                METHOD_NEITHER    = 3,
            }

            /// <summary>
            /// Indicates the type of access that a caller must request when opening the file object that represents the device.
            /// </summary>
            internal enum IOCTL_ACCESS : byte {

                /// <summary>
                /// The I/O manager sends the IRP for any caller that has a handle to the file object that represents the target device object.
                /// </summary>
                FILE_ANY_ACCESS   = 0,

                /// <summary>
                /// The I/O manager sends the IRP only for a caller with read access rights, allowing the underlying device driver to transfer data from the device to system memory.
                /// </summary>
                FILE_READ_DATA    = 1,

                /// <summary>
                /// The I/O manager sends the IRP only for a caller with write access rights, allowing the underlying device driver to transfer data from system memory to its device.
                /// </summary>
                FILE_WRITE_DATA   = 2,

                /// <summary>
                /// The caller must have both read and write access rights
                /// </summary>
                FILE_READ_WRITE_DATA = FILE_READ_DATA | FILE_WRITE_DATA,
            }

            /// <summary>
            /// Retrieves the calling thread's last-error code value.
            /// </summary>
            /// <returns>Calling thread's last-error code</returns>
            [DllImport("kernel32.dll")]
            internal static extern UInt16 GetLastError();
        }

        /// <summary>
        /// Advanced Windows 32 Base API (services)
        /// </summary>
        private static class Advapi32 {

            /// <summary>
            /// Establishes a connection to the service control manager on <paramref name="machineName"/> and opens the specified service control manager <paramref name="databaseName"/>.
            /// </summary>
            /// <param name="machineName">The name of the target computer</param>
            /// <param name="databaseName">The name of the service control manager database</param>
            /// <param name="dwAccess">The access to the service control manager</param>
            /// <returns>If the function succeeds, the return value is a handle to the specified service control manager database. If the function fails, the return value is NULL</returns>
            [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
            internal static extern IntPtr OpenSCManager(
                string machineName,
                string databaseName,
                ServiceAccessRights dwAccess);

            /// <summary>
            /// Establishes a connection to the service control manager on local computer and opens the specified service control manager SERVICES_ACTIVE_DATABASE database.
            /// </summary>
            /// <param name="dwAccess">The access to the service control manager</param>
            /// <returns>If the function succeeds, the return value is a handle to the specified service control manager database. If the function fails, the return value is NULL</returns>
            internal static IntPtr OpenSCManager(ServiceAccessRights dwAccess) {
                return OpenSCManager(null, null, dwAccess);
            }

            /// <summary>
            /// Service Security and Access Rights for the Service Control Manager
            /// </summary>
            internal enum ServiceAccessRights : UInt32 {
                SC_MANAGER_ALL_ACCESS         = 0xF003F,
                SC_MANAGER_CREATE_SERVICE     = 0x00002,
                SC_MANAGER_CONNECT            = 0x00001,
                SC_MANAGER_ENUMERATE_SERVICE  = 0x00004,
                SC_MANAGER_LOCK               = 0x00008,
                SC_MANAGER_MODIFY_BOOT_CONFIG = 0x00020,
                SC_MANAGER_QUERY_LOCK_STATUS  = 0x00010,
            }

            /// <summary>
            /// Creates an NT service object and adds it to the specified service control manager database
            /// </summary>
            /// <param name="hSCManager">A handle to the service control manager database</param>
            /// <param name="lpServiceName">The name of the service to install</param>
            /// <param name="lpDisplayName">The display name to be used by user interface programs to identify the service</param>
            /// <param name="dwDesiredAccess">The access to the service. For a list of values, see <see cref="ServiceAccessRights"/> values.</param>
            /// <param name="dwServiceType">The service type. This parameter can be one of the <see cref="ServiceType"/> values</param>
            /// <param name="dwStartType">The service start options. This parameter can be one of the <see cref="StartType"/> values</param>
            /// <param name="dwErrorControl">The severity of the error, and action taken, if this service fails to start. This parameter can be one of the <see cref="ErrorControl"/> values</param>
            /// <param name="lpBinaryPathName">The fully qualified path to the service binary file</param>
            /// <param name="lpLoadOrderGroup">The names of the load ordering group of which this service is a member</param>
            /// <param name="lpdwTagId">A pointer to a variable that receives a tag value that is unique in the group specified in the <paramref name="lpLoadOrderGroup"/> parameter</param>
            /// <param name="lpDependencies">A pointer to a double null-terminated array of null-separated names of services or load ordering groups that the system must start before this service</param>
            /// <param name="lpServiceStartName">The name of the account under which the service should run</param>
            /// <param name="lpPassword">The password to the account name specified by the <paramref name="lpServiceStartName"/> parameter</param>
            /// <returns>If the function succeeds, the return value is a handle to the service.</returns>
            [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
            internal static extern IntPtr CreateService(
                IntPtr hSCManager,
                string lpServiceName,
                string lpDisplayName,
                ServiceAccessRights dwDesiredAccess,
                ServiceType dwServiceType,
                StartType dwStartType,
                ErrorControl dwErrorControl,
                string lpBinaryPathName,
                [Optional] string lpLoadOrderGroup,
                [Optional] string lpdwTagId,    // only string so we can pass null
                [Optional] string lpDependencies,
                [Optional] string lpServiceStartName,
                [Optional] string lpPassword);

            /// <summary>
            /// The service type
            /// </summary>
            internal enum ServiceType : UInt32 {

                /// <summary>
                /// Driver service
                /// </summary>
                SERVICE_KERNEL_DRIVER       = 0x00000001,

                /// <summary>
                /// File system driver service
                /// </summary>
                SERVICE_FILE_SYSTEM_DRIVER  = 0x00000002,

                /// <summary>
                /// Reserved
                /// </summary>
                SERVICE_ADAPTER             = 0x00000004,

                /// <summary>
                /// Reserved
                /// </summary>
                SERVICE_RECOGNIZER_DRIVER   = 0x00000008,

                /// <summary>
                /// Service that runs in its own process
                /// </summary>
                SERVICE_WIN32_OWN_PROCESS   = 0x00000010,

                /// <summary>
                /// Service that shares a process with one or more other services
                /// </summary>
                SERVICE_WIN32_SHARE_PROCESS = 0x00000020,

                /// <summary>
                /// The service can interact with the desktop
                /// </summary>
                SERVICE_INTERACTIVE_PROCESS = 0x00000100,
            }

            /// <summary>
            /// The service start options
            /// </summary>
            internal enum StartType : UInt32 {
                /// <summary>
                /// A device driver started by the system loader. This value is valid only for driver services
                /// </summary>
                SERVICE_BOOT_START          = 0x00000000,

                /// <summary>
                /// A device driver started by the IoInitSystem function. This value is valid only for driver services
                /// </summary>
                SERVICE_SYSTEM_START        = 0x00000001,

                /// <summary>
                /// A service started automatically by the service control manager during system startup
                /// </summary>
                SERVICE_AUTO_START          = 0x00000002,

                /// <summary>
                /// A service started by the service control manager when a process calls the <see cref="StartService"/> function
                /// </summary>
                SERVICE_DEMAND_START        = 0x00000003,

                /// <summary>
                /// A service that cannot be started. Attempts to start the service result in the error code ERROR_SERVICE_DISABLED
                /// </summary>
                SERVICE_DISABLED            = 0x00000004,
            }

            /// <summary>
            /// The severity of the error, and action taken, if this service fails to start
            /// </summary>
            internal enum ErrorControl : UInt32 {
                
                /// <summary>
                /// The startup program ignores the error and continues the startup operation
                /// </summary>
                SERVICE_ERROR_IGNORE        = 0x00000000,
                
                /// <summary>
                /// The startup program logs the error in the event log but continues the startup operation
                /// </summary>
                SERVICE_ERROR_NORMAL        = 0x00000001,
                
                /// <summary>
                /// The startup program logs the error in the event log.
                /// If the last-known-good configuration is being started, the startup operation continues.
                /// Otherwise, the system is restarted with the last-known-good configuration.
                /// </summary>
                SERVICE_ERROR_SEVERE        = 0x00000002,

                /// <summary>
                /// The startup program logs the error in the event log, if possible
                /// </summary>
                SERVICE_ERROR_CRITICAL      = 0x00000003,
            }

            /// <summary>
            /// Windows error codes returned by <see cref="Marshal.GetHRForLastWin32Error"/>
            /// </summary>
            internal enum WinError {
                /// <summary>
                /// The operation completed successfully
                /// </summary>
                NO_ERROR                    = unchecked((int)0x80070000),
                /// <summary>
                /// The specified service already exists
                /// </summary>
                SERVICE_EXISTS              = unchecked((int)0x80070431),
                /// <summary>
                /// An instance of the service is already running
                /// </summary>
                SERVICE_ALREADY_RUNNING     = unchecked((int)0x80070420),
            }

            /// <summary>
            /// Marks the specified service for deletion from the service control manager database.
            /// </summary>
            /// <param name="hService">A handle to the service.
            /// This handle is returned by the <see cref="OpenService"/> or <see cref="CreateService"/> function, and it must have the DELETE access right.</param>
            /// <returns><see langref="true"/> if the function succeeds</returns>
            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DeleteService(IntPtr hService);

            /// <summary>
            /// Starts a service.
            /// </summary>
            /// <param name="hService">A handle to the service.
            /// This handle is returned by the <see cref="OpenService"/> or <see cref="CreateService"/> function,
            /// and it must have the <see cref="ServiceRights.SERVICE_START"/> access right.</param>
            /// <param name="dwNumServiceArgs">The number of strings in the <paramref name="lpServiceArgVectors"/> array.</param>
            /// <param name="lpServiceArgVectors">The null-terminated strings to be passed to the ServiceMain function for the service as arguments.</param>
            /// <returns><see langref="true"/> if the function succeeds</returns>
            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool StartService(
                IntPtr hService,
                int dwNumServiceArgs,
                string[] lpServiceArgVectors
            );

            /// <summary>
            /// Opens an existing service.
            /// </summary>
            /// <param name="hSCManager"></param>
            /// <param name="lpServiceName"></param>
            /// <param name="dwDesiredAccess"></param>
            /// <returns>If the function succeeds, the return value is a handle to the service. If the function fails, the return value is <see cref="IntPtr.Zero"/>.</returns>
            [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, CharSet = CharSet.Unicode)]
            internal static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, ServiceRights dwDesiredAccess);

            /// <summary>
            /// Specific access rights for a service
            /// </summary>
            internal enum ServiceRights : uint {

                /// <summary>
                /// Required to call the QueryServiceConfig and QueryServiceConfig2 functions to query the service configuration.
                /// </summary>
                SERVICE_QUERY_CONFIG         = 0x00001,

                /// <summary>
                /// Required to call the ChangeServiceConfig or ChangeServiceConfig2 function to change the service configuration. 
                /// </summary>
                SERVICE_CHANGE_CONFIG        = 0x00002,

                /// <summary>
                /// Required to call the QueryServiceStatus or QueryServiceStatusEx function to ask the service control manager about the status of the service.
                /// </summary>
                SERVICE_QUERY_STATUS         = 0x00004,

                /// <summary>
                /// Required to call the EnumDependentServices function to enumerate all the services dependent on the service.
                /// </summary>
                SERVICE_ENUMERATE_DEPENDENTS = 0x00008,

                /// <summary>
                /// Required to call the <see cref="StartService"/> function to start the service.
                /// </summary>
                SERVICE_START                = 0x00010,

                /// <summary>
                /// Required to call the <see cref="ControlService"/> function to stop the service.
                /// </summary>
                SERVICE_STOP                 = 0x00020,

                /// <summary>
                /// Required to call the <see cref="ControlService"/> function to pause or continue the service.
                /// </summary>
                SERVICE_PAUSE_CONTINUE       = 0x00040,

                /// <summary>
                /// Required to call the <see cref="ControlService"/> function to ask the service to report its status immediately.
                /// </summary>
                SERVICE_INTERROGATE          = 0x00080,

                /// <summary>
                /// Required to call the <see cref="ControlService"/> function to specify a user-defined control code.
                /// </summary>
                SERVICE_USER_DEFINED_CONTROL = 0x00100,

                /// <summary>
                /// The right to delete the object.
                /// </summary>
                DELETE                       = 0x10000,

                /// <summary>
                /// The right to read the information in the object's security descriptor, not including the information in the system access control list (SACL).
                /// </summary>
                READ_CONTROL                 = 0x20000,

                /// <summary>
                /// The right to modify the discretionary access control list (DACL) in the object's security descriptor.
                /// </summary>
                WRITE_DAC                    = 0x40000,

                /// <summary>
                /// The right to change the owner in the object's security descriptor.
                /// </summary>
                WRITE_OWNER                  = 0x80000,

                /// <summary>
                /// Combines DELETE, READ_CONTROL, WRITE_DAC, and WRITE_OWNER access.
                /// </summary>
                STANDARD_RIGHTS_REQUIRED     = DELETE | READ_CONTROL | WRITE_DAC | WRITE_OWNER,

                /// <summary>
                /// Includes <see cref="STANDARD_RIGHTS_REQUIRED"/> in addition to all access rights in this table.
                /// </summary>
                SERVICE_ALL_ACCESS           = STANDARD_RIGHTS_REQUIRED |
                                               SERVICE_QUERY_CONFIG |
                                               SERVICE_CHANGE_CONFIG |
                                               SERVICE_QUERY_STATUS |
                                               SERVICE_ENUMERATE_DEPENDENTS |
                                               SERVICE_START |
                                               SERVICE_STOP |
                                               SERVICE_PAUSE_CONTINUE |
                                               SERVICE_INTERROGATE |
                                               SERVICE_USER_DEFINED_CONTROL,
            }

            /// <summary>
            /// Sends a control code to a service.
            /// </summary>
            /// <param name="hService">A handle to the service.</param>
            /// <param name="dwControlCode">This parameter can be one of the <see cref="ServiceControlCode"/> control codes.</param>
            /// <param name="lpServiceStatus">A pointer to a <see cref="ServiceStatus"/> structure that receives the latest service status information.</param>
            /// <returns><see langref="true"/> if the function succeeds</returns>
            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool ControlService(IntPtr hService, ServiceControlCode dwControlCode, ref ServiceStatus lpServiceStatus);

            /// <summary>
            /// Control codes for <see cref="ControlService"/>
            /// </summary>
            internal enum ServiceControlCode : UInt32 {

                /// <summary>
                /// Notifies a paused service that it should resume.
                /// </summary>
                SERVICE_CONTROL_CONTINUE       = 0x00000003,

                /// <summary>
                /// Notifies a service that it should report its current status information to the service control manager.
                /// </summary>
                SERVICE_CONTROL_INTERROGATE    = 0x00000004,

                /// <summary>
                /// Notifies a network service that there is a new component for binding.
                /// </summary>
                SERVICE_CONTROL_NETBINDADD     = 0x00000007,

                /// <summary>
                /// Notifies a network service that one of its bindings has been disabled.
                /// </summary>
                SERVICE_CONTROL_NETBINDDISABLE = 0x0000000A,

                /// <summary>
                /// Notifies a network service that a disabled binding has been enabled.
                /// </summary>
                SERVICE_CONTROL_NETBINDENABLE  = 0x00000009,

                /// <summary>
                /// Notifies a network service that a component for binding has been removed.
                /// </summary>
                SERVICE_CONTROL_NETBINDREMOVE  = 0x00000008,

                /// <summary>
                /// Notifies a service that its startup parameters have changed.
                /// </summary>
                SERVICE_CONTROL_PARAMCHANGE    = 0x00000006,

                /// <summary>
                /// Notifies a service that it should pause.
                /// </summary>
                SERVICE_CONTROL_PAUSE          = 0x00000002,

                /// <summary>
                /// Notifies a service that it should stop.
                /// </summary>
                SERVICE_CONTROL_STOP           = 0x00000001,
            }

            /// <summary>
            /// Contains status information for a service.
            /// </summary>
            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            internal struct ServiceStatus {
                public ServiceStatusServiceType dwServiceType;
                public ServiceStatusCurrentState dwCurrentState;
                public ServiceStatusControlsAccepted dwControlsAccepted;
                public uint dwWin32ExitCode;
                public uint dwServiceSpecificExitCode;
                public uint dwCheckPoint;
                public uint dwWaitHint;
            }

            /// <summary>
            /// The type of service for <see cref="ServiceStatus"/>.
            /// </summary>
            internal enum ServiceStatusServiceType : UInt32 {

                /// <summary>
                /// The service is a file system driver. 
                /// </summary>
                SERVICE_FILE_SYSTEM_DRIVER     = 0x00000002,

                /// <summary>
                /// The service is a device driver. 
                /// </summary>
                SERVICE_KERNEL_DRIVER          = 0x00000001,

                /// <summary>
                /// The service runs in its own process. 
                /// </summary>
                SERVICE_WIN32_OWN_PROCESS      = 0x00000010,

                /// <summary>
                /// The service shares a process with other services. 
                /// </summary>
                SERVICE_WIN32_SHARE_PROCESS    = 0x00000020,

                /// <summary>
                /// The service runs in its own process under the logged-on user account. 
                /// </summary>
                SERVICE_USER_OWN_PROCESS       = 0x00000050,

                /// <summary>
                /// The service shares a process with one or more other services that run under the logged-on user account. 
                /// </summary>
                SERVICE_USER_SHARE_PROCESS     = 0x00000060,
            }

            /// <summary>
            /// The current state of the service for <see cref="ServiceStatus"/>. 
            /// </summary>
            internal enum ServiceStatusCurrentState : UInt32 {

                /// <summary>
                /// The service continue is pending. 
                /// </summary>
                SERVICE_CONTINUE_PENDING       = 0x00000005,

                /// <summary>
                /// The service pause is pending. 
                /// </summary>
                SERVICE_PAUSE_PENDING          = 0x00000006,

                /// <summary>
                /// The service is paused.
                /// </summary>
                SERVICE_PAUSED                 = 0x00000007,

                /// <summary>
                /// The service is running. 
                /// </summary>
                SERVICE_RUNNING                = 0x00000004,

                /// <summary>
                /// The service is starting. 
                /// </summary>
                SERVICE_START_PENDING          = 0x00000002,

                /// <summary>
                /// The service is stopping. 
                /// </summary>
                SERVICE_STOP_PENDING           = 0x00000003,

                /// <summary>
                /// The service is not running. 
                /// </summary>
                SERVICE_STOPPED                = 0x00000001,
            }

            /// <summary>
            /// The control codes the service accepts and processes in its handler function for <see cref="ServiceStatus"/>.
            /// </summary>
            internal enum ServiceStatusControlsAccepted : UInt32 {

                /// <summary>
                /// The service is a network component that can accept changes in its binding without being stopped and restarted.
                /// </summary>
                SERVICE_ACCEPT_NETBINDCHANGE   = 0x00000010,

                /// <summary>
                /// The service can reread its startup parameters without being stopped and restarted.
                /// </summary>
                SERVICE_ACCEPT_PARAMCHANGE     = 0x00000008,

                /// <summary>
                /// The service can be paused and continued.
                /// </summary>
                SERVICE_ACCEPT_PAUSE_CONTINUE  = 0x00000002,

                /// <summary>
                /// The service can perform preshutdown tasks.
                /// </summary>
                SERVICE_ACCEPT_PRESHUTDOWN     = 0x00000100,

                /// <summary>
                /// The service is notified when system shutdown occurs.
                /// </summary>
                SERVICE_ACCEPT_SHUTDOWN        = 0x00000004,

                /// <summary>
                /// The service can be stopped.
                /// </summary>
                SERVICE_ACCEPT_STOP            = 0x00000001,
            }

            /// <summary>
            /// Closes a handle to a service control manager or service object
            /// </summary>
            /// <param name="hSCObject">A handle to the service control manager object or the service object to close.
            /// Handles to service control manager objects are returned by the <see cref="OpenSCManager(string,string,ServiceAccessRights)"/> function,
            /// and handles to service objects are returned by either the <see cref="OpenService"/> or <see cref="CreateService"/> function.</param>
            /// <returns><see langref="true"/> if the function succeeds</returns>
            [DllImport("advapi32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool CloseServiceHandle(IntPtr hSCObject);
        }
    }
}