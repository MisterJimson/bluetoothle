﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reactive.Linq;
using CoreBluetooth;
using Foundation;


namespace Acr.Ble
{
    public class Device : AbstractDevice
    {
        readonly BleContext context;
        readonly CBPeripheral peripheral;


        public Device(BleContext context, CBPeripheral peripheral) : base(peripheral.Name, peripheral.Identifier.ToGuid())
        {
            this.context = context;
            this.peripheral = peripheral;
        }


        public override ConnectionStatus Status
        {
            get
            {
                switch (this.peripheral.State)
                {
                    case CBPeripheralState.Connected:
                        return ConnectionStatus.Connected;

                    case CBPeripheralState.Connecting:
                        return ConnectionStatus.Connecting;

                    case CBPeripheralState.Disconnecting:
                        return ConnectionStatus.Disconnecting;

                    case CBPeripheralState.Disconnected:
                    default:
                        return ConnectionStatus.Disconnected;
                }
            }
        }


        public override IObservable<object> Connect()
        {
            return Observable.Create<object>(ob =>
            {
                IDisposable sub1 = null;
                IDisposable sub2 = null;

                if (this.Status == ConnectionStatus.Connected)
                {
                    ob.Respond(null);
                }
                else
                {
                    sub1 = this.context
                        .PeripheralConnected
                        .Where(x => x.Equals(this.peripheral))
                        .Subscribe(x => ob.Respond(null));

                    sub2 = this.context
                        .FailedConnection
                        .Where(x => x.Peripheral.Equals(this.peripheral))
                        .Subscribe(x => ob.OnError(new Exception(x.Error.ToString())));

                    this.context.Manager.ConnectPeripheral(this.peripheral, new PeripheralConnectionOptions
                    {
                        NotifyOnDisconnection = true,
#if __IOS__ || __TVOS__
                        NotifyOnConnection = true,
                        NotifyOnNotification = true
#endif
                    });
                }

                return () =>
                {
                    sub1?.Dispose();
                    sub2?.Dispose();
                };
            });
        }


        public override void Disconnect()
        {
            this.context.Manager.CancelPeripheralConnection(this.peripheral);
        }


        IObservable<string> nameOb;
        public override IObservable<string> WhenNameUpdated()
        {
            this.nameOb = this.nameOb ?? Observable.Create<string>(ob =>
            {
                ob.OnNext(this.Name);
                var handler = new EventHandler((sender, args) => ob.OnNext(this.Name));
                this.peripheral.UpdatedName += handler;

                return () => this.peripheral.UpdatedName -= handler;
            })
            .Publish()
            .RefCount();

            return this.nameOb;
        }


        IObservable<ConnectionStatus> statusOb;
        public override IObservable<ConnectionStatus> WhenStatusChanged()
        {
            this.statusOb = this.statusOb ?? Observable.Create<ConnectionStatus>(ob =>
            {
                ob.OnNext(this.Status);

                var sub1 = this.context
                    .PeripheralConnected
                    .Where(x => x.Equals(this.peripheral))
                    .Subscribe(x => ob.OnNext(this.Status));

                var sub2 = this.context
                    .PeripheralDisconnected
                    .Where(x => x.Equals(this.peripheral))
                    .Subscribe(x => ob.OnNext(this.Status));

                return () =>
                {
                    sub1.Dispose();
                    sub2.Dispose();
                };
            })
            .Replay(1)
            .RefCount();

            return this.statusOb;
        }


        IObservable<IGattService> serviceOb;
        public override IObservable<IGattService> WhenServiceDiscovered()
        {
            this.serviceOb = this.serviceOb ?? Observable.Create<IGattService>(ob =>
            {
                Debug.WriteLine("Hooked for services for device " + this.Uuid);
                var services = new Dictionary<Guid, IGattService>();

                var handler = new EventHandler<NSErrorEventArgs>((sender, args) =>
                {
                    if (this.peripheral.Services == null)
                        return;

                    foreach (var native in this.peripheral.Services)
                    {
                        var service = new GattService(this, native);
                        if (!services.ContainsKey(service.Uuid))
                        {
                            services.Add(service.Uuid, service);
                            ob.OnNext(service);
                        }
                    }
                });
                this.peripheral.DiscoveredService += handler;

                var sub = this.WhenStatusChanged()
                    .Where(x => x == ConnectionStatus.Connected)
                    .Subscribe(_ =>
                    {
                        this.peripheral.DiscoverServices();
                        Debug.WriteLine("DiscoverServices for device " + this.Uuid);
                    });

                return () =>
                {
                    sub.Dispose();
                    this.peripheral.DiscoveredService -= handler;
                };
            })
            .ReplayWithReset(this
                .WhenStatusChanged()
                .Skip(1)
                .Where(x => x == ConnectionStatus.Disconnected)
            )
            .RefCount();

            return this.serviceOb;
        }


        public override IObservable<int> WhenRssiUpdated(TimeSpan? timeSpan)
        {
            var ts = timeSpan ?? TimeSpan.FromSeconds(3);

            return Observable.Create<int>(ob =>
            {
                var handler = new EventHandler<CBRssiEventArgs>((sender, args) => ob.OnNext(args.Rssi?.Int32Value ?? 0));
                this.peripheral.RssiRead += handler;
                var innerOb = Observable
                    .Interval(ts)
                    .Where(x => this.Status == ConnectionStatus.Connected)
                    .Subscribe(_ => this.peripheral.ReadRSSI());

                return () =>
                {
                    innerOb.Dispose();
                    this.peripheral.RssiRead -= handler;
                };
            });
        }


        public override IObservable<int> WhenMtuChanged()
        {
            return Observable.Return(this.GetCurrentMtuSize());
        }


        public override int GetCurrentMtuSize()
        {
            return (int)this.peripheral.GetMaximumWriteValueLength(CBCharacteristicWriteType.WithResponse);
        }


        public override int GetHashCode()
        {
            return this.peripheral.GetHashCode();
        }


        public override bool Equals(object obj)
        {
            var other = obj as Device;
            if (other == null)
                return false;

            if (!this.peripheral.Equals(other.peripheral))
                return false;

            return false;
        }


        public override string ToString()
        {
            return this.Uuid.ToString();
        }
    }
}