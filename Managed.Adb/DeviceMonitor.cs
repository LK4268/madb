﻿// <copyright file="DeviceMonitor.cs" company="The Android Open Source Project, Ryan Conrad, Quamotion">
// Copyright (c) The Android Open Source Project, Ryan Conrad, Quamotion. All rights reserved.
// </copyright>

namespace Managed.Adb
{
    using MoreLinq;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;

    /// <summary>
    /// A Device monitor. This connects to the Android Debug Bridge and get device and
    /// debuggable process information from it.
    /// </summary>
    public class DeviceMonitor : IDeviceMonitor, IDisposable
    {
        /// <summary>
        /// Logging tag
        /// </summary>
        private const string Tag = nameof(DeviceMonitor);

        private readonly List<DeviceData> devices;

        private readonly ManualResetEvent firstDeviceListParsed = new ManualResetEvent(false);

        /// <summary>
        /// The thread that monitors the <see cref="Socket"/> and waits for device notifications.
        /// </summary>
        private Thread monitorThread;

        /// <summary>
        /// Initializes a new instance of the <see cref="DeviceMonitor"/> class.
        /// </summary>
        /// <param name="socket">
        /// The <see cref="IAdbSocket"/> that manages the connection with the adb server.
        /// </param>
        public DeviceMonitor(IAdbSocket socket)
        {
            if (socket == null)
            {
                throw new ArgumentNullException(nameof(socket));
            }

            this.Socket = socket;
            this.devices = new List<DeviceData>();
            this.Devices = this.devices.AsReadOnly();
        }

        /// <include file='IDeviceMonitor.xml' path='/IDeviceMonitor/DeviceChanged/*'/>
        public event EventHandler<DeviceDataEventArgs> DeviceChanged;

        /// <include file='IDeviceMonitor.xml' path='/IDeviceMonitor/DeviceConnected/*'/>
        public event EventHandler<DeviceDataEventArgs> DeviceConnected;

        /// <include file='IDeviceMonitor.xml' path='/IDeviceMonitor/DeviceDisconnected/*'/>
        public event EventHandler<DeviceDataEventArgs> DeviceDisconnected;

        /// <include file='IDeviceMonitor.xml' path='/IDeviceMonitor/Devices/*'/>
        public IReadOnlyCollection<DeviceData> Devices { get; private set; }

        /// <summary>
        /// Gets the <see cref="IAdbSocket"/> that represents the connection to the
        /// Android Debug Bridge.
        /// </summary>
        public IAdbSocket Socket { get; private set; }

        /// <summary>
        /// Gets a value indicating whether this instance is running.
        /// </summary>
        /// <value>
        /// <see langword="true"/> if this instance is running; otherwise, <see langword="false"/>.
        /// </value>
        public bool IsRunning { get; private set; }

        /// <include file='IDeviceMonitor.xml' path='/IDeviceMonitor/Start/*'/>
        public void Start()
        {
            if (this.monitorThread == null)
            {
                this.firstDeviceListParsed.Reset();

                this.monitorThread = new Thread(new ThreadStart(this.DeviceMonitorLoop));
                this.monitorThread.Name = "Managed.Adb - Device List Monitor";
                this.monitorThread.Start();

                // Wait for the worker thread to have read the first list
                // of devices.
                this.firstDeviceListParsed.WaitOne();
            }
        }

        /// <summary>
        /// Stops the monitoring
        /// </summary>
        public void Dispose()
        {
            // Close the connection to adb.
            // This will also cause the socket to disconnect, and the
            // monitor thread to cancel out (because an ObjectDisposedException is thrown
            // on the GetString method and subsequently Socket.Connected = false and Socket = null).
            if (this.Socket != null)
            {
                this.Socket.Dispose();
                this.Socket = null;
            }

            if (this.monitorThread != null)
            {
                this.IsRunning = false;

                // Wait for the monitor thread to stop.
                this.monitorThread.Join();

                this.monitorThread = null;
            }
        }

        /// <summary>
        /// Raises the <see cref="DeviceChanged"/> event.
        /// </summary>
        /// <param name="e">The <see cref="DeviceDataEventArgs"/> instance containing the event data.</param>
        protected void OnDeviceChanged(DeviceDataEventArgs e)
        {
            if (this.DeviceChanged != null)
            {
                this.DeviceChanged(this, e);
            }
        }

        /// <summary>
        /// Raises the <see cref="DeviceConnected"/> event.
        /// </summary>
        /// <param name="e">The <see cref="DeviceDataEventArgs"/> instance containing the event data.</param>
        protected void OnDeviceConnected(DeviceDataEventArgs e)
        {
            if (this.DeviceConnected != null)
            {
                this.DeviceConnected(this, e);
            }
        }

        /// <summary>
        /// Raises the <see cref="DeviceDisconnected"/> event.
        /// </summary>
        /// <param name="e">The <see cref="DeviceDataEventArgs"/> instance containing the event data.</param>
        protected void OnDeviceDisconnected(DeviceDataEventArgs e)
        {
            if (this.DeviceDisconnected != null)
            {
                this.DeviceDisconnected(this, e);
            }
        }

        /// <summary>
        /// Monitors the devices. This connects to the Debug Bridge
        /// </summary>
        private void DeviceMonitorLoop()
        {
            this.IsRunning = true;

            // Set up the connection to track the list of devices.
            this.Socket.SendAdbRequest("host:track-devices");
            this.Socket.ReadAdbResponse(false);

            do
            {
                try
                {
                    var value = this.Socket.ReadStringAsync().Result;
                    this.ProcessIncomingDeviceData(value);

                    this.firstDeviceListParsed.Set();
                }
                catch (Exception ex)
                {
                    Log.e(Tag, ex);
                }
            }
            while (this.Socket != null && this.Socket.Connected);
        }

        /// <summary>
        /// Processes the incoming device data.
        /// </summary>
        private void ProcessIncomingDeviceData(string result)
        {
            List<DeviceData> list = new List<DeviceData>();

            string[] devices = result.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            devices.ForEach(d =>
            {
                try
                {
                    var dv = DeviceData.CreateFromAdbData(d);
                    if (dv != null)
                    {
                        list.Add(dv);
                    }
                }
                catch (ArgumentException ae)
                {
                    Log.e(Tag, ae);
                }
            });

            // now merge the new devices with the old ones.
            this.UpdateDevices(list);
        }

        private void UpdateDevices(List<DeviceData> list)
        {
            lock (this.Devices)
            {
                // For each device in the current list, we look for a matching the new list.
                // * if we find it, we update the current object with whatever new information
                //   there is
                //   (mostly state change, if the device becomes ready, we query for build info).
                //   We also remove the device from the new list to mark it as "processed"
                // * if we do not find it, we remove it from the current list.
                // Once this is done, the new list contains device we aren't monitoring yet, so we
                // add them to the list, and start monitoring them.

                // Add or update existing devices
                foreach (var device in list)
                {
                    var existingDevice = this.Devices.SingleOrDefault(d => d.Serial == device.Serial);

                    if (existingDevice == null)
                    {
                        this.devices.Add(device);
                        this.OnDeviceConnected(new DeviceDataEventArgs(device));
                    }
                    else
                    {
                        existingDevice.State = device.State;
                        this.OnDeviceChanged(new DeviceDataEventArgs(existingDevice));
                    }
                }

                // Remove devices
                foreach (var device in this.Devices.Where(d => !list.Any(e => e.Serial == d.Serial)).ToArray())
                {
                    this.devices.Remove(device);
                    this.OnDeviceDisconnected(new DeviceDataEventArgs(device));
                }
            }
        }
    }
}
