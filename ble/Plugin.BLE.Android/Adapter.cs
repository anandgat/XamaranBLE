using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.OS;
using Java.Util;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Extensions;
using Object = Java.Lang.Object;
using Trace = Plugin.BLE.Abstractions.Trace;
using Android.App;
using System.IO;

namespace Plugin.BLE.Android
{
    public class Adapter : AdapterBase
    {
        private readonly BluetoothManager _bluetoothManager;
        private readonly BluetoothAdapter _bluetoothAdapter;
        private readonly Api18BleScanCallback _api18ScanCallback;
        private readonly Api21BleScanCallback _api21ScanCallback;
        private BluetoothSocket bluetoothSocket;
       
        private Stream reader;
        private Stream writer;

        public override IList<IDevice> ConnectedDevices => ConnectedDeviceRegistry.Values.ToList();

        /// <summary>
        /// Used to store all connected devices
        /// </summary>
        public Dictionary<string, IDevice> ConnectedDeviceRegistry { get; }

        public Adapter(BluetoothManager bluetoothManager)
        {
            _bluetoothManager = bluetoothManager;
            _bluetoothAdapter = bluetoothManager.Adapter;

            ConnectedDeviceRegistry = new Dictionary<string, IDevice>();

            // TODO: bonding
            //var bondStatusBroadcastReceiver = new BondStatusBroadcastReceiver();
            //Application.Context.RegisterReceiver(bondStatusBroadcastReceiver,
            //    new IntentFilter(BluetoothDevice.ActionBondStateChanged));

            ////forward events from broadcast receiver
            //bondStatusBroadcastReceiver.BondStateChanged += (s, args) =>
            //{
            //    //DeviceBondStateChanged(this, args);
            //};

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
            {
                _api21ScanCallback = new Api21BleScanCallback(this);
            }
            else
            {
                _api18ScanCallback = new Api18BleScanCallback(this);
            }
        }

        protected override Task StartScanningForDevicesNativeAsync(Guid[] serviceUuids, bool allowDuplicatesKey, CancellationToken scanCancellationToken)
        {
            // clear out the list
            DiscoveredDevices.Clear();

            if (Build.VERSION.SdkInt < BuildVersionCodes.Lollipop)
            {
                StartScanningOld(serviceUuids);
            }
            else
            {
                StartScanningNew(serviceUuids);
            }

            return Task.FromResult(true);
        }


        protected override async Task<string> GetResponseFromSocketNativeAsync(string request)
        {
            return await SendAndReceive(request);
        }



        private async Task<string> SendAndReceive(string msg)
        {
            await WriteAsync(msg);
            string s = await ReadAsync();
            System.Diagnostics.Debug.WriteLine("Received: " + s);
           // s = s.Replace("SEARCHING...\r\n", "");
            return s;
        }

        private async Task WriteAsync(string msg)
        {
            System.Diagnostics.Debug.WriteLine(msg);
            byte[] buffer = GetBytes(msg);
            await writer.WriteAsync(buffer, 0, buffer.Length);
        }

        private byte[] GetBytes(string str)
        {
            byte[] bytes = new byte[str.Length * sizeof(char)];
            Buffer.BlockCopy(str.ToCharArray(), 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private async Task<string> ReadAsync()
        {
            string ret = await ReadAsyncRaw();
            while (!ret.Trim().EndsWith(">"))
            {
                string tmp = await ReadAsyncRaw();
                ret = ret + tmp;
            }
            return ret;
        }

        private async Task<string> ReadAsyncRaw()
        {
            byte[] buffer = new byte[1024];
            var bytes = await reader.ReadAsync(buffer, 0, buffer.Length);
            var s1 = new Java.Lang.String(buffer, 0, bytes);
            var s = s1.ToString();
            System.Diagnostics.Debug.WriteLine(s);
            return s;
        }


        private void StartScanningOld(Guid[] serviceUuids)
        {
            var hasFilter = serviceUuids?.Any() ?? false;
            UUID[] uuids = null;
            if (hasFilter)
            {
                uuids = serviceUuids.Select(u => UUID.FromString(u.ToString())).ToArray();
            }
            Trace.Message("Adapter < 21: Starting a scan for devices.");
#pragma warning disable 618
            _bluetoothAdapter.StartLeScan(uuids, _api18ScanCallback);
#pragma warning restore 618
        }

        private void StartScanningNew(Guid[] serviceUuids)
        {
            var hasFilter = serviceUuids?.Any() ?? false;
            List<ScanFilter> scanFilters = null;

            if (hasFilter)
            {
                scanFilters = new List<ScanFilter>();
                foreach (var serviceUuid in serviceUuids)
                {
                    var sfb = new ScanFilter.Builder();
                    sfb.SetServiceUuid(ParcelUuid.FromString(serviceUuid.ToString()));
                    scanFilters.Add(sfb.Build());
                }
            }

            var ssb = new ScanSettings.Builder();
            ssb.SetScanMode(ScanMode.ToNative());
            //ssb.SetCallbackType(ScanCallbackType.AllMatches);

            if (_bluetoothAdapter.BluetoothLeScanner != null)
            {
                Trace.Message($"Adapter >=21: Starting a scan for devices. ScanMode: {ScanMode}");
                if (hasFilter)
                {
                    Trace.Message($"ScanFilters: {string.Join(", ", serviceUuids)}");
                }
                _bluetoothAdapter.BluetoothLeScanner.StartScan(scanFilters, ssb.Build(), _api21ScanCallback);
            }
            else
            {
                Trace.Message("Adapter >= 21: Scan failed. Bluetooth is probably off");
            }
        }

        protected override void StopScanNative()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.Lollipop)
            {
                Trace.Message("Adapter < 21: Stopping the scan for devices.");
#pragma warning disable 618
                _bluetoothAdapter.StopLeScan(_api18ScanCallback);
#pragma warning restore 618
            }
            else
            {
                Trace.Message("Adapter >= 21: Stopping the scan for devices.");
                _bluetoothAdapter.BluetoothLeScanner?.StopScan(_api21ScanCallback);
            }
        }

        protected override Task ConnectToDeviceNativeAsync(IDevice device, ConnectParameters connectParameters,
            CancellationToken cancellationToken)
        {
            bool isBond = false;
            BluetoothDevice nativeDevice=null;
           var devices = _bluetoothAdapter.BondedDevices;

            if(devices!=null && devices.Count>0)
            {
               
                foreach (var item in devices)
                {

                    string name = item.Name;
                    if(name.Contains("OBD"))
                    {
                        nativeDevice = item;
                        isBond = true;
                       var uuids =  item.GetUuids();
                    }
                }
            }
            if (isBond)
            {
                connectRfComm(nativeDevice);
                if(bluetoothSocket.IsConnected)
                {
                    HandleConnectedDeviceBluetooth(device);
                }
                else{
                    HandleDisconnectedDevice(true, device);
                }
                return Task.CompletedTask;
            }
            else
            {
                ((Device)device).Connect(connectParameters, cancellationToken);
                return Task.CompletedTask;
            }
        }

        private void connectRfComm(BluetoothDevice bluetoothDevice)
        {
            try
            {

                if (bluetoothSocket == null)
                    bluetoothSocket = bluetoothDevice.CreateRfcommSocketToServiceRecord(UUID.FromString("00001101-0000-1000-8000-00805F9B34FB"));
                if(!bluetoothSocket.IsConnected)
                    bluetoothSocket.Connect();
            }
            catch (Java.IO.IOException e)
            {
                // Close the socket
                try
                {
                    bluetoothSocket.Close();
                }
                catch (Java.IO.IOException e1)
                {
                }
                catch (Exception e2)
                {
                }

            }
            catch (Exception e)
            {

            }

            if(bluetoothSocket.IsConnected)
            {
                reader = bluetoothSocket.InputStream;
                writer = bluetoothSocket.OutputStream;
            }

        }

        protected override void DisconnectDeviceNative(IDevice device)
        {
            //make sure everything is disconnected
            BluetoothDevice nativeDevice = (BluetoothDevice)device.NativeDevice;
            if (nativeDevice.BondState == Bond.Bonded)
            {
                DisconnectBondedDevice();
            }
            else
            {
                ((Device)device).Disconnect();
            }
        }

        private void DisconnectBondedDevice()
        {
            if (reader != null)
            {
                reader.Close();
                reader = null;
            }
            if (writer != null)
            {
                writer.Close();
                writer = null;
            }
            if (bluetoothSocket != null)
            {
                try
                {
                    bluetoothSocket.Close();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(ex.Message);
                }
                bluetoothSocket = null;
            }
        }
        public override async Task<IDevice> ConnectToKnownDeviceAsync(Guid deviceGuid, ConnectParameters connectParameters = default(ConnectParameters), CancellationToken cancellationToken = default(CancellationToken))
        {
            var macBytes = deviceGuid.ToByteArray().Skip(10).Take(6).ToArray();
            var nativeDevice = _bluetoothAdapter.GetRemoteDevice(macBytes);

            var device = new Device(this, nativeDevice, null, 0, new byte[] { });

            await ConnectToDeviceAsync(device, connectParameters, cancellationToken);
            return device;
        }

        public override List<IDevice> GetSystemConnectedOrPairedDevices(Guid[] services = null)
        {
            if (services != null)
            {
                Trace.Message("Caution: GetSystemConnectedDevices does not take into account the 'services' parameter on Android.");
            }

            //add dualMode type too as they are BLE too ;)
            var connectedDevices = _bluetoothManager.GetConnectedDevices(ProfileType.Gatt).Where(d => d.Type == BluetoothDeviceType.Le || d.Type == BluetoothDeviceType.Dual);

            var bondedDevices = _bluetoothAdapter.BondedDevices.Where(d => d.Type == BluetoothDeviceType.Le || d.Type == BluetoothDeviceType.Dual);

            return connectedDevices.Union(bondedDevices, new DeviceComparer()).Select(d => new Device(this, d, null, 0)).Cast<IDevice>().ToList();
        }

        private class DeviceComparer : IEqualityComparer<BluetoothDevice>
        {
            public bool Equals(BluetoothDevice x, BluetoothDevice y)
            {
                return x.Address == y.Address;
            }

            public int GetHashCode(BluetoothDevice obj)
            {
                return obj.GetHashCode();
            }
        }


        public class Api18BleScanCallback : Object, BluetoothAdapter.ILeScanCallback
        {
            private readonly Adapter _adapter;

            public Api18BleScanCallback(Adapter adapter)
            {
                _adapter = adapter;
            }

            public void OnLeScan(BluetoothDevice bleDevice, int rssi, byte[] scanRecord)
            {
                Trace.Message("Adapter.LeScanCallback: " + bleDevice.Name);

                _adapter.HandleDiscoveredDevice(new Device(_adapter, bleDevice, null, rssi, scanRecord));
            }
        }


        public class Api21BleScanCallback : ScanCallback
        {
            private readonly Adapter _adapter;
            public Api21BleScanCallback(Adapter adapter)
            {
                _adapter = adapter;
            }

            public override void OnScanFailed(ScanFailure errorCode)
            {
                Trace.Message("Adapter: Scan failed with code {0}", errorCode);
                base.OnScanFailed(errorCode);
            }

            public override void OnScanResult(ScanCallbackType callbackType, ScanResult result)
            {
                base.OnScanResult(callbackType, result);

                /* Might want to transition to parsing the API21+ ScanResult, but sort of a pain for now 
                List<AdvertisementRecord> records = new List<AdvertisementRecord>();
                records.Add(new AdvertisementRecord(AdvertisementRecordType.Flags, BitConverter.GetBytes(result.ScanRecord.AdvertiseFlags)));
                if (!string.IsNullOrEmpty(result.ScanRecord.DeviceName))
                {
                    records.Add(new AdvertisementRecord(AdvertisementRecordType.CompleteLocalName, Encoding.UTF8.GetBytes(result.ScanRecord.DeviceName)));
                }
                for (int i = 0; i < result.ScanRecord.ManufacturerSpecificData.Size(); i++)
                {
                    int key = result.ScanRecord.ManufacturerSpecificData.KeyAt(i);
                    var arr = result.ScanRecord.GetManufacturerSpecificData(key);
                    byte[] data = new byte[arr.Length + 2];
                    BitConverter.GetBytes((ushort)key).CopyTo(data,0);
                    arr.CopyTo(data, 2);
                    records.Add(new AdvertisementRecord(AdvertisementRecordType.ManufacturerSpecificData, data));
                }

                foreach(var uuid in result.ScanRecord.ServiceUuids)
                {
                    records.Add(new AdvertisementRecord(AdvertisementRecordType.UuidsIncomplete128Bit, uuid.Uuid.));
                }

                foreach(var key in result.ScanRecord.ServiceData.Keys)
                {
                    records.Add(new AdvertisementRecord(AdvertisementRecordType.ServiceData, result.ScanRecord.ServiceData));
                }*/

                var device = new Device(_adapter, result.Device, null, result.Rssi, result.ScanRecord.GetBytes());

                //Device device;
                //if (result.ScanRecord.ManufacturerSpecificData.Size() > 0)
                //{
                //    int key = result.ScanRecord.ManufacturerSpecificData.KeyAt(0);
                //    byte[] mdata = result.ScanRecord.GetManufacturerSpecificData(key);
                //    byte[] mdataWithKey = new byte[mdata.Length + 2];
                //    BitConverter.GetBytes((ushort)key).CopyTo(mdataWithKey, 0);
                //    mdata.CopyTo(mdataWithKey, 2);
                //    device = new Device(result.Device, null, null, result.Rssi, mdataWithKey);
                //}
                //else
                //{
                //    device = new Device(result.Device, null, null, result.Rssi, new byte[0]);
                //}

                _adapter.HandleDiscoveredDevice(device);

            }
        }
    }
}



