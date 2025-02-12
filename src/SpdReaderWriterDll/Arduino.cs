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
using System.IO.Ports;
using System.Linq;
using System.Text;
using UInt8 = System.Byte;

namespace SpdReaderWriterDll {

    /// <summary>
    /// Defines Device class, properties, and methods to handle the communication with the device
    /// </summary>
    public class Arduino : IDisposable {

        /// <summary>
        /// Initializes the SPD reader/writer device
        /// </summary>
        /// <param name="portSettings">Serial port settings</param>
        public Arduino(SerialPortSettings portSettings) {
            PortSettings = portSettings;
        }

        /// <summary>
        /// Initializes the SPD reader/writer device
        /// </summary>
        /// <param name="portSettings">Serial port settings</param>
        /// <param name="portName">Serial port name</param>
        public Arduino(SerialPortSettings portSettings, string portName) {
            PortSettings = portSettings;
            PortName     = portName;
        }

        /// <summary>
        /// Initializes the SPD reader/writer device
        /// </summary>
        /// <param name="portSettings">Serial port settings</param>
        /// <param name="portName">Serial port name</param>
        /// <param name="i2cAddress">EEPROM address on the device's i2c bus</param>
        public Arduino(SerialPortSettings portSettings, string portName, UInt8 i2cAddress) {
            PortSettings = portSettings;
            PortName     = portName;
            I2CAddress   = i2cAddress;
        }

        /// <summary>
        /// Initializes the SPD reader/writer device
        /// </summary>
        /// <param name="portSettings">Serial port settings</param>
        /// <param name="portName">Serial port name</param>
        /// <param name="i2cAddress">EEPROM address on the device's i2c bus</param>
        /// <param name="dataLength">Total EEPROM size</param>
        public Arduino(SerialPortSettings portSettings, string portName, UInt8 i2cAddress, Spd.DataLength dataLength) {
            PortSettings = portSettings;
            PortName     = portName;
            I2CAddress   = i2cAddress;
            DataLength   = dataLength;
        }

        /// <summary>
        /// Device instance string
        /// </summary>
        /// <returns>Device instance string</returns>
        public override string ToString() {
            string _string = "";
            if (PortName != null) {
                _string += $"{PortName}";
                if (I2CAddress != 0) {
                    _string += $":{I2CAddress}";
                }
            }            

            return _string.Trim();
        }

        /// <summary>
        /// Device class destructor
        /// </summary>
        ~Arduino() {
            Dispose();
        }

        /// <summary>
        /// Serial Port Settings class
        /// </summary>
        public struct SerialPortSettings {
            // Connection settings
            public int BaudRate;
            public bool DtrEnable;
            public bool RtsEnable;

            // Response settings
            public int ResponseTimeout;

            /// <summary>
            /// Default port settings
            /// </summary>
            /// <param name="baudRate">Baud rate</param>
            /// <param name="dtrEnable">Enable DTR</param>
            /// <param name="rtsEnable">Enable RTS</param>
            /// <param name="responseTimeout">Response timeout in seconds</param>
            public SerialPortSettings(
                int baudRate        = 115200,
                bool dtrEnable      = true,
                bool rtsEnable      = true,
                int responseTimeout = 10) {
                        BaudRate        = baudRate;
                        DtrEnable       = dtrEnable;
                        RtsEnable       = rtsEnable;
                        ResponseTimeout = responseTimeout;
            }

            /// <summary>
            /// Serial port settings string
            /// </summary>
            /// <returns>Serial port settings string</returns>
            public override string ToString() {
                return $"{BaudRate}";
            }
        }

        /// <summary>
        /// Attempts to establish a connection with the SPD reader/writer device
        /// </summary>
        /// <returns><see langword="true"/> if the connection is established</returns>
        public bool Connect() {
            lock (_portLock) {
                if (!IsConnected) {
                    _sp = new SerialPort {
                        // New connection settings
                        PortName = PortName,
                        BaudRate = PortSettings.BaudRate,
                        DtrEnable = PortSettings.DtrEnable,
                        RtsEnable = PortSettings.RtsEnable
                    };

                    // Event to handle Data Reception
                    _sp.DataReceived += DataReceivedHandler;

                    // Event to handle Errors
                    _sp.ErrorReceived += ErrorReceivedHandler;

                    // Test the connection
                    try {
                        // Establish a connection
                        _sp.Open();

                        // Set valid state to true to allow Communication Test to execute
                        _isValid = true;
                        try {
                            _isValid = Test();
                        }
                        catch {
                            _isValid = false;
                            Dispose();
                        }

                        if (!_isValid) {
                            try {
                                Dispose();
                            }
                            finally {
                                throw new Exception("Invalid device");
                            }
                        }
                    }
                    catch (Exception ex) {
                        throw new Exception($"Unable to connect ({PortName}): {ex.Message}");
                    }
                }
            }
            return IsConnected;
        }

        /// <summary>
        /// Disconnects the SPD reader/writer device
        /// </summary>
        /// <returns><see langword="true"/> once the device is disconnected</returns>
        public bool Disconnect() {
            lock (_portLock) {
                if (IsConnected) {
                    try {
                        // Remove handlers
                        _sp.DataReceived -= DataReceivedHandler;
                        _sp.ErrorReceived -= ErrorReceivedHandler;
                        // Close connection
                        _sp.Close();
                        // Reset valid state
                        _isValid = false;
                    }
                    catch (Exception ex) {
                        throw new Exception($"Unable to disconnect ({PortName}): {ex.Message}");
                    }

                }

                return !IsConnected;
            }
        }

        /// <summary>
        /// Disposes device instance
        /// </summary>
        public void Dispose() {
            lock (_portLock) {
                if (_sp != null && _sp.IsOpen) {
                    _sp.Close();
                    _sp = null;
                }
                DataReceiving = false;
                IsValid = false;
                ResponseData.Clear();
            }
        }

        /// <summary>
        /// Tests if the device responds to a test command
        /// </summary>
        /// <returns><see langword="true"/> if the device responds properly to a test command</returns>
        public bool Test() {
            lock (_portLock) {
                try {
                    return IsConnected &&
                           ExecuteCommand(Command.TESTCOMM) == Response.WELCOME;
                }
                catch {
                    throw new Exception($"Unable to test {PortName}");
                }
            }
        }

        /// <summary>
        /// Gets supported RAM type(s)
        /// </summary>
        /// <returns>A bitmask representing available RAM supported defined in the <see cref="Response.RswpSupport"/> struct</returns>
        public byte GetRamTypeSupport() {
            lock (_portLock) {
                try {
                    return ExecuteCommand(Command.RSWPREPORT);
                }
                catch {
                    throw new Exception($"Unable to get {PortName} supported RAM");
                }
            }
        }

        /// <summary>
        /// Test if the device supports RAM type RSWP at firmware level
        /// </summary>
        /// <param name="ramTypeBitmask">RAM type bitmask</param>
        /// <returns><see langword="true"/> if the device supports <see cref="Response.RswpSupport"/> RSWP at firmware level</returns>
        public bool GetRamTypeSupport(byte ramTypeBitmask) {
            return (GetRamTypeSupport() & ramTypeBitmask) == ramTypeBitmask;
        }

        /// <summary>
        /// Re-evaluate device's RSWP capabilities
        /// </summary>
        /// <returns>A bitmask representing available RAM supported defined in the <see cref="Response.RswpSupport"/> struct</returns>
        public byte RswpRetest() {
            lock (_portLock) {
                try {
                    return ExecuteCommand(Command.RETESTRSWP);
                }
                catch {
                    throw new Exception($"Unable to get {PortName} supported RAM");
                }
            }
        }

        /// <summary>
        /// Reads a byte from the device
        /// </summary>
        /// <returns>A single byte value received from the device</returns>
        public byte ReadByte() {
            return (byte)_sp.ReadByte();
        }

        /// <summary>
        /// Scans the device for I2C bus devices
        /// </summary>
        /// <returns>An array of addresses on the device's I2C bus</returns>
        public UInt8[] Scan() {
            Queue<UInt8> addresses = new Queue<UInt8>();

            lock (_portLock) {
                try {
                    if (IsConnected) {
                        byte response = Scan(true);

                        if (response == Response.NULL) {
                            return new byte[0];
                        }

                        for (UInt8 i = 0; i <= 7; i++) {
                            if (Data.GetBit(response, i)) {
                                addresses.Enqueue((byte)(80 + i));
                            }
                        }
                    }
                }
                catch {
                    throw new Exception($"Unable to scan I2C bus on {PortName}");
                }
            }

            return addresses.ToArray();
        }

        /// <summary>
        /// Scans for EEPROM addresses on the device's I2C bus
        /// </summary>
        /// <param name="bitmask">Enable bitmask response</param>
        /// <returns>A bitmask representing available addresses on the device's I2C bus. Bit 0 is address 80, bit 1 is address 81, and so on.</returns>
        public UInt8 Scan(bool bitmask) {
            if (bitmask) {
                lock (_portLock) {
                    try {
                        if (IsConnected) {
                            return ExecuteCommand(Command.SCANBUS);
                        }
                    }
                    catch {
                        throw new Exception($"Unable to scan I2C bus on {PortName}");
                    }
                }
            }

            return 0;
        }

        /// <summary>
        /// Sets clock frequency for I2C communication
        /// </summary>
        /// <param name="fastMode">Fast mode or standard mode</param>
        /// <returns><see langword="true"/> if the operation is successful</returns>
        public bool SetI2CClock(bool fastMode) {
            lock (_portLock) {
                try {
                    return IsConnected &&
                           ExecuteCommand(new[] { Command.I2CCLOCK, Data.BoolToInt(fastMode) }) == Response.SUCCESS;
                }
                catch {
                    throw new Exception($"Unable to set I2C clock mode on {PortName}");
                }
            }
        }

        /// <summary>
        /// Gets current device I2C clock mode
        /// </summary>
        /// <returns><see langword="true"/> if the device's I2C bus is running in fast mode,
        /// or <see langword="false"/> if it is running in standard mode</returns>
        public bool GetI2CClock() {
            lock (_portLock) {
                try {
                    return IsConnected &&
                           ExecuteCommand(new[] { Command.I2CCLOCK, Command.GET }) == Response.SUCCESS;
                }
                catch {
                    throw new Exception($"Unable to get I2C clock mode on {PortName}");
                }
            }
        }

        /// <summary>
        /// Gets current device I2C clock
        /// </summary>
        public UInt16 I2CClock => (UInt16)(GetI2CClock() ? 400 : 100);

        /// <summary>
        /// Resets the device's settings to defaults
        /// </summary>
        /// <returns><see langword="true"/> once the device's settings are successfully reset to defaults</returns>
        public bool FactoryReset() {
            lock (_portLock) {
                try {
                    if (IsConnected &&
                        ExecuteCommand(new[] { Command.FACTORYRESET }) == Response.SUCCESS) {

                        return true;
                    }

                    return false;
                }
                catch {
                    throw new Exception($"Unable to reset device settings on {PortName}");
                }
            }
        }

        /// <summary>
        /// Gets or sets SA1 control pin
        /// </summary>
        public bool PIN_SA1 {
            get => GetConfigPin(Pin.Name.SA1_SWITCH);
            set => SetConfigPin(Pin.Name.SA1_SWITCH, value);
        }

        /// <summary>
        /// Gets or sets DDR5 offline mode control pin
        /// </summary>
        public bool PIN_OFFLINE {
            get => GetConfigPin(Pin.Name.OFFLINE_MODE_SWITCH);
            set => SetOfflineMode(value);
        }

        /// <summary>
        /// Gets or sets High voltage control pin
        /// </summary>
        public bool PIN_VHV {
            get => GetHighVoltage();
            set => SetHighVoltage(value);
        }

        /// <summary>
        /// Controls high voltage state on pin SA0
        /// </summary>
        /// <param name="state">High voltage supply state</param>
        /// <returns><see langword="true"/> when operation is successful</returns>
        public bool SetHighVoltage(bool state) {
            lock (_portLock) {
                try {
                    return IsConnected &&
                           ExecuteCommand(new[] { Command.PINCONTROL, Pin.Name.HIGH_VOLTAGE_SWITCH, Data.BoolToInt(state) }) == Response.SUCCESS;
                }
                catch {
                    throw new Exception($"Unable to set High Voltage state on {PortName}");
                }
            }
        }

        /// <summary>
        /// Gets high voltage state on pin SA0
        /// </summary>
        /// <returns><see langword="true"/> if high voltage is applied to pin SA0</returns>
        public bool GetHighVoltage() {
            lock (_portLock) {
                try {
                    return IsConnected &&
                           ExecuteCommand(new[] { Command.PINCONTROL, Pin.Name.HIGH_VOLTAGE_SWITCH, Command.GET }) == Response.ON;
                }
                catch {
                    throw new Exception($"Unable to get High Voltage state on {PortName}");
                }
            }
        }

        /// <summary>
        /// Sets specified configuration pin to desired state
        /// </summary>
        /// <param name="pin">Pin name</param>
        /// <param name="state">Pin state</param>
        /// <returns><see langword="true"/> if the config pin has been set</returns>
        public bool SetConfigPin(byte pin, bool state) {
            lock (_portLock) {
                try {
                    return IsConnected &&
                           ExecuteCommand(new[] { Command.PINCONTROL, pin, Data.BoolToInt(state) }) == Response.SUCCESS;
                }
                catch {
                    throw new Exception($"Unable to set config pin state on {PortName}");
                }
            }
        }

        /// <summary>
        /// Get specified configuration pin state
        /// </summary>
        /// <returns><see langword="true"/> if pin is high, or <see langword="false"/> when pin is low</returns>
        public bool GetConfigPin(byte pin) {
            lock (_portLock) {
                try {
                    return IsConnected &&
                           ExecuteCommand(new[] { Command.PINCONTROL, pin, Command.GET }) == Response.ON;
                }
                catch {
                    throw new Exception($"Unable to get config pin state on {PortName}");
                }
            }
        }

        /// <summary>
        /// Controls DDR5 offline mode operation
        /// </summary>
        /// <param name="state">Offline mode state</param>
        /// <returns><see langword="true"/> when operation completes successfully</returns>
        public bool SetOfflineMode(bool state) {
            lock (_portLock) {
                try {
                    return ExecuteCommand(new[] { Command.PINCONTROL, Pin.Name.OFFLINE_MODE_SWITCH, Data.BoolToInt(state) }) == Response.SUCCESS;
                }
                catch {
                    throw new Exception($"Unable to set offline mode on {PortName}");
                }
            }
        }

        /// <summary>
        /// Gets DDR5 offline mode status
        /// </summary>
        /// <returns><see langword="true"/> when DDR5 is in offline mode</returns>
        public bool GetOfflineMode() {
            lock (_portLock) {
                try {
                    return ExecuteCommand(new[] { Command.PINCONTROL, Pin.Name.OFFLINE_MODE_SWITCH, Command.GET }) == Response.SUCCESS;
                }
                catch {
                    throw new Exception($"Unable to get offline mode status on {PortName}");
                }
            }
        }

        /// <summary>
        /// Resets all config pins to their default state
        /// </summary>
        /// <returns><see langword="true"/> when all config pins are reset</returns>
        public bool ResetAddressPins() {

            PIN_SA1     = Pin.State.DEFAULT;
            PIN_VHV     = Pin.State.DEFAULT;
            PIN_OFFLINE = Pin.State.DEFAULT;

            return !PIN_SA1 && !PIN_VHV && !PIN_OFFLINE;
        }

        /// <summary>
        /// Probes default EEPROM address
        /// </summary>
        /// <returns><see langword="true"/> if EEPROM is detected at the specified address</returns>
        public bool ProbeAddress() {
            return I2CAddress != 0 && ProbeAddress(I2CAddress);
        }

        /// <summary>
        /// Probes specified EEPROM address
        /// </summary>
        /// <param name="address">EEPROM address</param>
        /// <returns><see langword="true"/> if EEPROM is detected at the specified address</returns>
        public bool ProbeAddress(UInt8 address) {
            lock (_portLock) {
                try {
                    return IsConnected &&
                           ExecuteCommand(new[] { Command.PROBEADDRESS, address }) == Response.ACK;
                }
                catch {
                    throw new Exception($"Unable to probe address {address} on {PortName}");
                }
            }
        }

        /// <summary>
        /// Clears serial port buffers from unneeded data to prevent unwanted behavior and delays
        /// </summary>
        public void ClearBuffer() {
            lock (_portLock) {
                try {
                    if (IsConnected) {
                        // Clear response data
                        if (ResponseData.Count > 0) {
                            ResponseData.Clear();
                        }

                        // Clear receive buffer
                        if (BytesToRead > 0) {
                            _sp.DiscardInBuffer();
                        }

                        // Clear transmit buffer
                        if (BytesToWrite > 0) {
                            _sp.DiscardOutBuffer();
                        }
                    }
                }
                catch {
                    throw new Exception($"Unable to clear {PortName} buffer");
                }
            }
        }

        /// <summary>
        /// Clears serial port buffers and causes any buffered data to be written
        /// </summary>
        public void FlushBuffer() {
            lock (_portLock) {
                if (IsConnected) {
                    _sp.BaseStream.Flush();
                }
            }
        }

        /// <summary>
        /// Executes a single byte command on the device and expects a single byte response
        /// </summary>
        /// <param name="command">Byte to be sent to the device</param>
        /// <returns>A byte received from the device in response</returns>
        public byte ExecuteCommand(byte command) {
            return ExecuteCommand(this, new []{ command }, 1)[0];
        }

        /// <summary>
        /// Executes a multi byte command on the device and expects a single byte response
        /// </summary>
        /// <param name="command">Bytes to be sent to the device</param>
        /// <returns>A byte received from the device in response</returns>
        public byte ExecuteCommand(byte[] command) {
            return ExecuteCommand(this, command, 1)[0];
        }

        /// <summary>
        /// Executes a single byte command on the device and expects a multi byte response
        /// </summary>
        /// <param name="command">Byte to be sent to the device</param>
        /// <param name="length">Number of bytes to receive in response</param>
        /// <returns>A byte array received from the device in response</returns>
        public byte[] ExecuteCommand(byte command, uint length) {
            return ExecuteCommand(this, new[] { command }, length);
        }

        /// <summary>
        /// Executes a multi byte command on the device and expects a multi byte response
        /// </summary>
        /// <param name="command">Bytes to be sent to the device</param>
        /// <param name="length">Number of bytes to receive in response</param>
        /// <returns>A byte array received from the device in response</returns>
        public byte[] ExecuteCommand(byte[] command, uint length) {
            return ExecuteCommand(this, command, length);
        }

        /// <summary>
        /// Device's firmware version
        /// </summary>
        public string FirmwareVersion => GetFirmwareVersion().ToString();

        /// <summary>
        /// Get device's firmware version 
        /// </summary>
        /// <returns>Firmware version number</returns>
        public int GetFirmwareVersion() {
            int version = 0;
            lock (_portLock) {
                try {
                    if (IsConnected) {
                        version = Int32.Parse(
                            Data.BytesToString(ExecuteCommand(Command.GETVERSION, 8))
                        );
                    }
                }
                catch {
                    throw new Exception($"Unable to get firmware version on {PortName}");
                }
            }
            return version;
        }

        /// <summary>
        /// Device's user assigned name
        /// </summary>
        public string Name {
            get => GetName();
            set => SetName(value);
        }

        /// <summary>
        /// Assigns a name to the Device
        /// </summary>
        /// <param name="name">Device name</param>
        /// <returns><see langword="true"/> when the device name is set</returns>
        public bool SetName(string name) {
            if (name == null) {
                throw new ArgumentNullException("Name can't be null");
            }
            if (name == "") {
                throw new ArgumentException("Name can't be blank");
            }
            if (name.Length > 16) {
                throw new ArgumentException("Name can't be longer than 16 characters");
            }

            lock (_portLock) {
                try {
                    if (IsConnected) {
                        string newName = name.Trim();

                        if (newName == GetName()) {
                            return false;
                        }

                        // Prepare a byte array containing cmd byte + name length + name
                        byte[] nameCommand = new byte[1 + 1 + newName.Length];
                        // Command byte at position 0
                        nameCommand[0] = Command.NAME;
                        // Name length at position 1
                        nameCommand[1] = (byte)newName.Length;
                        // Copy new name to byte array
                        Array.Copy(Encoding.ASCII.GetBytes(newName), 0, nameCommand, 2, newName.Length);

                        return ExecuteCommand(nameCommand) == Response.SUCCESS;
                    }
                }
                catch {
                    throw new Exception($"Unable to assign name to {PortName}");
                }
            }

            return false;
        }

        /// <summary>
        /// Gets Device's name
        /// </summary>
        /// <returns>Device's name</returns>
        public string GetName() {
            lock (_portLock) {
                try {
                    if (IsConnected) {
                        return Data.BytesToString(ExecuteCommand(this, new[] { Command.NAME, Command.GET }, 16));
                    }
                }
                catch {
                    throw new Exception($"Unable to get {PortName} name");
                }
            }

            return null;
        }

        /// <summary>
        /// Finds devices connected to computer by sending a test command to every serial port device detected
        /// </summary>
        /// <returns>An array of serial port names which have valid devices connected to</returns>
        public string[] Find() {
            Stack<string> result = new Stack<string>();

            lock (_findLock) {
                foreach (string portName in SerialPort.GetPortNames().Distinct().ToArray()) {

                    Arduino device = new Arduino(PortSettings, portName);
                    try {
                        lock (device._portLock) {
                            if (device.Connect()) {
                                device.Dispose();
                                result.Push(portName);
                            }
                        }
                    }
                    catch {
                        continue;
                    }
                }
            }

            return result.ToArray();
        }

        /// <summary>
        /// Describes device's connection state
        /// </summary>
        public bool IsConnected {
            get {
                try {
                    return _sp != null && _sp.IsOpen && _isValid;
                }
                catch {
                    return false;
                }
            }
        }

        /// <summary>
        /// Describes if the device passed connection and communication tests
        /// </summary>
        public bool IsValid {
            get => _isValid;
            set => _isValid = value;
        }

        /// <summary>
        /// Detects if DDR4 RAM is present on the device's I2C bus
        /// </summary>
        /// <returns><see langword="true"/> if DDR4 is found</returns>
        public bool DetectDdr4() {
            return DetectDdr4(I2CAddress);
        }

        /// <summary>
        /// Detects if DDR4 RAM is present on the device's I2C bus at specified <see cref="address"/>
        /// </summary>
        /// <param name="address">I2C address</param>
        /// <returns><see langword="true"/> if DDR4 is found at <see cref="address"/></returns>
        public bool DetectDdr4(UInt8 address) {
            lock (_portLock) {
                try {
                    return IsConnected &&
                           ExecuteCommand(new[] { Command.DDR4DETECT, address }) == Response.SUCCESS;
                }
                catch {
                    throw new Exception($"Error detecting DDR4 on {PortName}");
                }
            }
        }

        /// <summary>
        /// Detects if DDR5 RAM is present on the device's I2C bus
        /// </summary>
        /// <returns><see langword="true"/> if DDR5 is found</returns>
        public bool DetectDdr5() {
            return DetectDdr5(I2CAddress);
        }

        /// <summary>
        /// Detects if DDR5 RAM is present on the device's I2C bus at specified <see cref="address"/>
        /// </summary>
        /// <param name="address">I2C address</param>
        /// <returns><see langword="true"/> if DDR5 is found at <see cref="address"/></returns>
        public bool DetectDdr5(UInt8 address) {
            lock (_portLock) {
                try {
                    return IsConnected &&
                           ExecuteCommand(new[] { Command.DDR5DETECT, address }) == Response.SUCCESS;
                }
                catch {
                    throw new Exception($"Error detecting DDR5 on {PortName}");
                }
            }
        }

        /// <summary>
        /// Serial Port connection and data settings
        /// </summary>
        public SerialPortSettings PortSettings;

        /// <summary>
        /// Serial port name the device is connected to
        /// </summary>
        public string PortName;

        /// <summary>
        /// EEPROM address
        /// </summary>
        public UInt8 I2CAddress;

        /// <summary>
        /// EEPROM size
        /// </summary>
        public Spd.DataLength DataLength;

        /// <summary>
        /// Number of bytes to be read from the device
        /// </summary>
        public int BytesToRead {
            get {
                try {
                    return _sp.BytesToRead;
                }
                catch {
                    return 0;
                }
            }
        }

        /// <summary>
        /// Number of bytes to be sent to the device
        /// </summary>
        public int BytesToWrite {
            get {
                try {
                    return _sp.BytesToWrite;
                }
                catch {
                    return 0;
                }
            }
        }

        /// <summary>
        /// Bitmask value representing RAM type supported defined in <see cref="Response.RswpSupport"/> enum
        /// </summary>
        public byte RamTypeSupport {
            get {
                try {
                    return GetRamTypeSupport();
                }
                catch {
                    throw new Exception("Unable to get supported RAM type");
                }
            }
        }
        
        /// <summary>
        /// Value representing whether the device supports RSWP capabilities based on RAM type supported reported by the device
        /// </summary>
        public bool RswpPresent {
            get {
                return IsConnected &&
                       ((RamTypeSupport & Response.RswpSupport.DDR3) == Response.RswpSupport.DDR3 ||
                        (RamTypeSupport & Response.RswpSupport.DDR4) == Response.RswpSupport.DDR4 ||
                        (RamTypeSupport & Response.RswpSupport.DDR5) == Response.RswpSupport.DDR5);
            }
        }

        /// <summary>
        /// Byte stack containing data received from Serial Port
        /// </summary>
        public static Queue<byte> ResponseData = new Queue<byte>();

        /// <summary>
        /// Value indicating whether data reception is complete
        /// </summary>
        public static bool DataReceiving = false;

        /// <summary>
        /// Data Received Handler which read data and puts it into ResponseData queue
        /// </summary>
        /// <param name="sender">Sender object</param>
        /// <param name="e">Event arguments</param>
        public static void DataReceivedHandler(object sender, SerialDataReceivedEventArgs e) {

            if (sender != null && sender.GetType() == typeof(SerialPort)) {
                while (((SerialPort)sender).IsOpen && ((SerialPort)sender).BytesToRead > 0) {
                    DataReceiving = true;
                    ResponseData.Enqueue((byte)((SerialPort)sender).ReadByte());
                }
                DataReceiving = false;
            }
        }

        /// <summary>
        /// Error Received Handler
        /// </summary>
        /// <param name="sender">Sender object</param>
        /// <param name="e">Event arguments</param>
        public static void ErrorReceivedHandler(object sender, SerialErrorReceivedEventArgs e) {

            if (sender != null && sender.GetType() == typeof(SerialPort)) {
                throw new Exception($"Error received: {((SerialPort)sender).PortName}");
            }
        }

        /// <summary>
        /// Serial port instance
        /// </summary>
        private SerialPort _sp = new SerialPort();

        /// <summary>
        /// Describes whether the device is valid
        /// </summary>
        private bool _isValid;

        /// <summary>
        /// Executes commands on the device.
        /// </summary>
        /// <param name="command">Bytes to be sent to the device</param>
        /// <param name="responseLength">Number of bytes to receive in response</param>
        /// <returns>A byte array received from the device in response</returns>
        private byte[] ExecuteCommand(Arduino device, byte[] command, uint responseLength) {
            if (command.Length == 0) {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(command));
            }

            if (!device.IsConnected) {
                throw new InvalidOperationException("Device is not connected");
            }

            byte[] response = new byte[responseLength];

            lock (_portLock) {
                try {
                    // Check connection
                    if (IsConnected) {
                        // Clear input and output buffers
                        ClearBuffer();

                        // Send the command to device
                        _sp.Write(command, 0, command.Length);

                        // Flush the buffer
                        FlushBuffer();

                        // Check response length
                        if (responseLength == 0) {
                            return new byte[0];
                        }

                        // Timeout monitoring start
                        Stopwatch sw = new Stopwatch();
                        sw.Start();

                        // Get response
                        while (PortSettings.ResponseTimeout * 1000 > sw.ElapsedMilliseconds) {
                            // Check connection
                            if (!IsConnected) {
                                throw new IOException($"{PortName} not connected");
                            }

                            // Wait for data
                            if (ResponseData != null && ResponseData.Count >= responseLength && !DataReceiving) {
                                for (int i = 0; i < response.Length; i++) {
                                    response[i] = ResponseData.Dequeue();
                                }
                                break;
                            }
                        }

                        return response;
                    }
                    throw new TimeoutException($"{PortName} response timeout");
                }
                catch {
                    throw new IOException($"{PortName} failed to execute command {command}");
                }
            }
        }

        /// <summary>
        /// PortLock object used to prevent other threads from acquiring the lock 
        /// </summary>
        private readonly object _portLock = new object();

        /// <summary>
        /// FindLock object used to prevent other threads from acquiring the lock 
        /// </summary>
        private readonly object _findLock = new object();

        /// <summary>
        /// Device commands
        /// </summary>
        public struct Command {
            /// <summary>
            /// Read byte
            /// </summary>
            public const byte READBYTE     = (byte) 'r';
            /// <summary>
            /// Write byte
            /// </summary>
            public const byte WRITEBYTE    = (byte) 'w';
            /// <summary>
            /// Write page
            /// </summary>
            public const byte WRITEPAGE    = (byte) 'g';
            /// <summary>
            /// Scan i2c bus
            /// </summary>
            public const byte SCANBUS      = (byte) 's';
            /// <summary>
            /// Set i2c clock 
            /// </summary>
            public const byte I2CCLOCK     = (byte) 'c';
            /// <summary>
            /// Probe i2c address
            /// </summary>
            public const byte PROBEADDRESS = (byte) 'a';
            /// <summary>
            /// Config pin state control
            /// </summary>
            public const byte PINCONTROL   = (byte) 'p';
            /// <summary>
            /// RSWP control
            /// </summary>
            public const byte RSWP         = (byte) 'b';
            /// <summary>
            /// PSWP control
            /// </summary>
            public const byte PSWP         = (byte) 'l';
            /// <summary>
            /// Get Firmware version
            /// </summary>
            public const byte GETVERSION   = (byte) 'v';
            /// <summary>
            /// Device Communication Test
            /// </summary>
            public const byte TESTCOMM     = (byte) 't';
            /// <summary>
            /// Report current RSWP RAM support
            /// </summary>
            public const byte RSWPREPORT   = (byte) 'f';
            /// <summary>
            /// Re-evaluate RSWP capabilities
            /// </summary>
            public const byte RETESTRSWP   = (byte) 'e';
            /// <summary>
            /// Device name controls
            /// </summary>
            public const byte NAME         = (byte) 'n';
            /// <summary>
            /// DDR4 detection
            /// </summary>
            public const byte DDR4DETECT   = (byte) '4';
            /// <summary>
            /// DDR5 detection
            /// </summary>
            public const byte DDR5DETECT   = (byte) '5';
            /// <summary>
            /// Restore device settings to default
            /// </summary>
            public const byte FACTORYRESET = (byte) '-';
            /// <summary>
            /// Suffix added to get current state
            /// </summary>
            public const byte GET          = (byte) '?';
            /// <summary>
            /// Suffix added to set state equivalent to true/on/enable etc
            /// </summary>
            public const byte ON           = 1;
            /// <summary>
            /// Suffix added to set state equivalent to false/off/disable etc
            /// </summary>
            public const byte OFF          = 0;
            /// <summary>
            /// "Do not care" byte
            /// </summary>
            public const byte DNC          = 0;
        }

        /// <summary>
        /// Class describing configuration pins
        /// </summary>
        public struct Pin {
            /// <summary>
            /// Struct describing config pin names
            /// </summary>
            public struct Name {
                /// <summary>
                /// DDR5 offline mode control pin
                /// </summary>
                public const byte OFFLINE_MODE_SWITCH = 0;

                /// <summary>
                /// Slave address 1 (SA1) control pin
                /// </summary>
                public const byte SA1_SWITCH          = 1;

                /// <summary>
                /// High voltage (9V) control pin
                /// </summary>
                public const byte HIGH_VOLTAGE_SWITCH = 9;
            }

            /// <summary>
            /// Struct describing config pin states
            /// </summary>
            public struct State {
                /// <summary>
                /// Pin state name describing condition when pin is <b>HIGH</b>
                /// </summary>
                public const bool HIGH     = true;

                /// <summary>
                /// Pin state name describing condition when pin is <b>LOW</b>
                /// </summary>
                public const bool LOW      = false;

                // Aliases for HIGH
                public const bool VDDSPD   = HIGH;
                public const bool PULLUP   = HIGH;
                public const bool VCC      = HIGH;
                public const bool ON       = HIGH;
                public const bool UP       = HIGH;
                public const bool ENABLE   = HIGH;
                public const bool ENABLED  = HIGH;

                // Aliases for LOW
                public const bool VSSSPD   = LOW;
                public const bool PUSHDOWN = LOW;
                public const bool VSS      = LOW;
                public const bool GND      = LOW;
                public const bool OFF      = LOW;
                public const bool DOWN     = LOW;
                public const bool DISABLE  = LOW;
                public const bool DISABLED = LOW;
                public const bool DEFAULT  = LOW;
            }
        }

        /// <summary>
        /// Responses received from the device
        /// </summary>
        public struct Response {
            /// <summary>
            /// Boolean True response
            /// </summary>
            public const byte TRUE     = 0x01;
            /// <summary>
            /// Boolean False response
            /// </summary>
            public const byte FALSE    = 0x00;
            /// <summary>
            /// Indicates the operation has failed
            /// </summary>
            public const byte ERROR    = 0xFF;
            /// <summary>
            /// Indicates the operation was executed successfully
            /// </summary>
            public const byte SUCCESS  = 0x01;
            /// <summary>
            /// A response used to indicate an error when normally a numeric non-zero answer is expected if the operation was executed successfully
            /// </summary>
            public const byte NULL     = 0x00;
            /// <summary>
            /// A response used to describe when SA pin is tied to VCC
            /// </summary> 
            public const byte ON       = 0x01;
            /// <summary>
            /// A response used to describe when SA pin is tied to GND
            /// </summary>
            public const byte OFF      = 0x00;
            /// <summary>
            /// A response expected from the device after executing <see cref="Command.TESTCOMM"/> command to identify the correct device
            /// </summary>
            public const char WELCOME  = '!';
            /// <summary>
            /// A response indicating the command or syntax was not in a correct format
            /// </summary>
            public const char UNKNOWN  = '?';

            // Aliases
            public const byte ACK      = SUCCESS;
            public const byte ENABLED  = TRUE;
            public const byte DISABLED = FALSE;
            public const byte NACK     = ERROR;
            public const byte NOACK    = ERROR;
            public const byte FAIL     = ERROR;
            public const byte ZERO     = NULL;

            /// <summary>
            /// Bitmask values describing specific RAM type RSWP support
            /// </summary>
            public struct RswpSupport {

                /// <summary>
                /// Value describing <value>DDR3</value> and below RSWP support
                /// </summary>
                public const byte DDR3 = 1 << 3;

                /// <summary>
                /// Value describing <value>DDR4</value> RSWP support
                /// </summary>
                public const byte DDR4 = 1 << 4;

                /// <summary>
                /// Value describing <value>DDR5</value> RSWP support
                /// </summary>
                public const byte DDR5 = 1 << 5;
            }
        }
    }
}