﻿using Android.Content;
using Android.Hardware.Usb;
using Device.Net;
using Java.Nio;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Usb.Net.Android
{
    public class AndroidUsbDevice : DeviceBase, IDevice
    {
        #region Fields
        private UsbDeviceConnection _UsbDeviceConnection;
        private UsbDevice _UsbDevice;
        private UsbEndpoint _WriteEndpoint;
        private UsbEndpoint _ReadEndpoint;
        private SemaphoreSlim _InitializingSemaphoreSlim = new SemaphoreSlim(1, 1);
        private bool _IsDisposing;
        #endregion

        #region Public Constants
        public const string LogSection = "AndroidHidDevice";
        #endregion

        #region Public Properties
        public bool IsInitialized => _UsbDeviceConnection != null;
        public UsbManager UsbManager { get; }
        public Context AndroidContext { get; private set; }
        public int TimeoutMilliseconds { get; }
        public override ushort ReadBufferSize => (ushort)_ReadEndpoint.MaxPacketSize;
        public override ushort WriteBufferSize => (ushort)_WriteEndpoint.MaxPacketSize;
        public int DeviceId { get; private set; }
        #endregion

        #region Constructor
        public AndroidUsbDevice(UsbManager usbManager, Context androidContext, int deviceId, int timeoutMilliseconds)
        {
            UsbManager = usbManager;
            AndroidContext = androidContext;
            TimeoutMilliseconds = timeoutMilliseconds;
            DeviceId = deviceId;
        }
        #endregion

        #region Public Methods 
        public override void Dispose()
        {
            if (_IsDisposing) return;
            _IsDisposing = true;

            try
            {

                _UsbDeviceConnection?.Dispose();
                _UsbDevice?.Dispose();
                _ReadEndpoint?.Dispose();
                _WriteEndpoint?.Dispose();

                _UsbDeviceConnection = null;
                _UsbDevice = null;
                _ReadEndpoint = null;
                _WriteEndpoint = null;

                base.Dispose();
            }
            catch (Exception ex)
            {
                //TODO: Logging
            }

            _IsDisposing = false;
        }

        //TODO: Make async properly
        public override async Task<byte[]> ReadAsync()
        {
            try
            {
                var byteBuffer = ByteBuffer.Allocate(ReadBufferSize);
                var request = new UsbRequest();
                request.Initialize(_UsbDeviceConnection, _ReadEndpoint);
                request.Queue(byteBuffer, ReadBufferSize);
                await _UsbDeviceConnection.RequestWaitAsync();
                var buffers = new byte[ReadBufferSize];

                byteBuffer.Rewind();
                for (var i = 0; i < ReadBufferSize; i++)
                {
                    buffers[i] = (byte)byteBuffer.Get();
                }

                //Marshal.Copy(byteBuffer.GetDirectBufferAddress(), buffers, 0, ReadBufferLength);

                Tracer?.Trace(false, buffers);

                return buffers;
            }
            catch (Exception ex)
            {
                Logger.Log(Helpers.ReadErrorMessage, ex, LogSection);
                throw new IOException(Helpers.ReadErrorMessage, ex);
            }
        }

        //TODO: Perhaps we should implement Batch Begin/Complete so that the UsbRequest is not created again and again. This will be expensive
        public override async Task WriteAsync(byte[] data)
        {
            try
            {
                var request = new UsbRequest();
                request.Initialize(_UsbDeviceConnection, _WriteEndpoint);
                var byteBuffer = ByteBuffer.Wrap(data);

                Tracer?.Trace(true, data);

                request.Queue(byteBuffer, data.Length);
                await _UsbDeviceConnection.RequestWaitAsync();
            }
            catch (Exception ex)
            {
                Logger.Log(Helpers.WriteErrorMessage, ex, LogSection);
                throw new IOException(Helpers.WriteErrorMessage, ex);
            }
        }

        #endregion

        #region Private  Methods
        private Task<bool?> RequestPermissionAsync()
        {
            Logger.Log("Requesting USB permission", null, LogSection);

            var taskCompletionSource = new TaskCompletionSource<bool?>();

            var usbPermissionBroadcastReceiver = new UsbPermissionBroadcastReceiver(UsbManager, _UsbDevice, AndroidContext);
            usbPermissionBroadcastReceiver.Received += (sender, eventArgs) =>
            {
                taskCompletionSource.SetResult(usbPermissionBroadcastReceiver.IsPermissionGranted);
            };

            usbPermissionBroadcastReceiver.Register();

            return taskCompletionSource.Task;
        }

        public async Task InitializeAsync()
        {
            try
            {
                await _InitializingSemaphoreSlim.WaitAsync();

                Dispose();

                _UsbDevice = UsbManager.DeviceList.Select(d => d.Value).FirstOrDefault(d => d.DeviceId == DeviceId);

                DeviceDefinition = AndroidUsbDeviceFactory.GetAndroidDeviceDefinition(_UsbDevice);

                if (_UsbDevice == null)
                {
                    throw new Exception($"The device {DeviceId} is not connected to the system");
                }

                var isPermissionGranted = await RequestPermissionAsync();
                if (!isPermissionGranted.HasValue)
                {
                    throw new Exception("User did not respond to permission request");
                }

                if (!isPermissionGranted.Value)
                {
                    throw new Exception("The user did not give the permission to access the device");
                }

                //TODO: This is the default interface but other interfaces might be needed so this needs to be changed.
                var usbInterface = _UsbDevice.GetInterface(0);

                //TODO: This selection stuff needs to be moved up higher. The constructor should take these arguments
                for (var i = 0; i < usbInterface.EndpointCount; i++)
                {
                    var ep = usbInterface.GetEndpoint(i);
                    if (_ReadEndpoint == null && ep.Type == UsbAddressing.XferInterrupt && ep.Address == (UsbAddressing)129)
                    {
                        _ReadEndpoint = ep;
                        continue;
                    }

                    if (_WriteEndpoint == null && ep.Type == UsbAddressing.XferInterrupt && (ep.Address == (UsbAddressing)1 || ep.Address == (UsbAddressing)2))
                    {
                        _WriteEndpoint = ep;
                    }
                }

                //TODO: This is a bit of a guess. It only kicks in if the previous code fails. This needs to be reworked for different devices
                if (_ReadEndpoint == null)
                {
                    _ReadEndpoint = usbInterface.GetEndpoint(0);
                }

                if (_WriteEndpoint == null)
                {
                    _WriteEndpoint = usbInterface.GetEndpoint(1);
                }

                if (_ReadEndpoint.MaxPacketSize != ReadBufferSize)
                {
                    throw new Exception("Wrong packet size for read endpoint");
                }

                if (_WriteEndpoint.MaxPacketSize != ReadBufferSize)
                {
                    throw new Exception("Wrong packet size for write endpoint");
                }

                _UsbDeviceConnection = UsbManager.OpenDevice(_UsbDevice);

                if (_UsbDeviceConnection == null)
                {
                    throw new Exception("could not open connection");
                }

                if (!_UsbDeviceConnection.ClaimInterface(usbInterface, true))
                {
                    throw new Exception("could not claim interface");
                }

                Logger.Log("Hid device initialized. About to tell everyone.", null, LogSection);

                RaiseConnected();

                return;
            }
            catch (Exception ex)
            {
                Logger.Log("Error initializing Hid Device", ex, LogSection);
                throw;
            }
            finally
            {
                _InitializingSemaphoreSlim.Release();
            }
        }
        #endregion
    }
}