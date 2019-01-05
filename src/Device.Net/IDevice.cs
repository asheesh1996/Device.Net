﻿using System;
using System.Threading.Tasks;

namespace Device.Net
{
    public interface IDevice : IDisposable
    {
        /// <summary>
        /// Occurs after the device has successfully connected. Note: this can be called multiple times and will occurr every time  InitializeAsync is called successfull. 
        /// </summary>
        event EventHandler Connected;

        /// <summary>
        /// Placeholder. This is not currently being used. Please do not rely on this at the moment.
        /// </summary>
        event EventHandler Disconnected;

        /// <summary>
        /// Whether or not the device has been successfully initialized
        /// </summary>
        bool IsInitialized { get; }

        /// <summary>
        /// Read a page of data. Warning: this is not thread safe. WriteAndReadAsync() should be preferred.
        /// </summary>
        Task<byte[]> ReadAsync();

        /// <summary>
        /// Write a page of data. Warning: this is not thread safe. WriteAndReadAsync() should be preferred.
        /// </summary>
        Task WriteAsync(byte[] data);

        /// <summary>
        /// Dispose of any existing connections and reinitialize the device. 
        /// </summary>
        Task InitializeAsync();

        /// <summary>
        /// Write a page of data and then wait for the device to return a page. If the implementation derives from DeviceBase, this method is thread safe.
        /// </summary>
        Task<byte[]> WriteAndReadAsync(byte[] writeBuffer);

        /// <summary>
        /// The unique identifier for the physical device. 
        /// </summary>
        string SerialNumber { get; }
    }
}