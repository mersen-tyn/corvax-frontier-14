using System.Linq;
using Content.Server.DeviceNetwork.Components;
using Content.Shared.DeviceNetwork;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.DeviceNetwork.Systems;
using Content.Shared.Interaction;
using JetBrains.Annotations;
using Robust.Shared.GameObjects;
using Robust.Shared.Map.Events;

namespace Content.Server.DeviceNetwork.Systems
{
    public sealed class DeviceListSystem : SharedDeviceListSystem
    {
        [Dependency] private readonly NetworkConfiguratorSystem _configurator = default!;
        [Dependency] private readonly IEntityManager _entityManager = default!;

        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<DeviceListComponent, ComponentShutdown>(OnShutdown);
            SubscribeLocalEvent<DeviceListComponent, BeforeBroadcastAttemptEvent>(OnBeforeBroadcast);
            SubscribeLocalEvent<DeviceListComponent, BeforePacketSentEvent>(OnBeforePacketSent);
            SubscribeLocalEvent<BeforeSaveEvent>(OnMapSave);
            _entityManager.EntityDeleted += OnEntityDeleted;
        }

        private void OnEntityDeleted(EntityUid uid)
        {
            var query = GetEntityQuery<DeviceListComponent>();
            foreach (var comp in query)
            {
                if (comp.Devices.Remove(uid))
                {
                    Dirty(comp.Owner, comp);
                    if (TryComp<DeviceNetworkComponent>(uid, out var deviceComp))
                    {
                        deviceComp.DeviceLists.Remove(comp.Owner);
                    }
                }
            }
        }

        public override void Shutdown()
        {
            base.Shutdown();
            _entityManager.EntityDeleted -= OnEntityDeleted;
        }

        private void OnShutdown(EntityUid uid, DeviceListComponent component, ComponentShutdown args)
        {
            foreach (var conf in component.Configurators)
            {
                _configurator.OnDeviceListShutdown(conf, (uid, component));
            }

            var query = GetEntityQuery<DeviceNetworkComponent>();
            foreach (var device in component.Devices)
            {
                if (query.TryGetComponent(device, out var comp))
                    comp.DeviceLists.Remove(uid);
            }
            component.Devices.Clear();
        }

        public Dictionary<string, EntityUid> GetDeviceList(EntityUid uid, DeviceListComponent? deviceList = null)
        {
            if (!Resolve(uid, ref deviceList))
                return new Dictionary<string, EntityUid>();

            var devices = new Dictionary<string, EntityUid>(deviceList.Devices.Count);
            foreach (var deviceUid in deviceList.Devices)
            {
                if (!TryComp<DeviceNetworkComponent>(deviceUid, out DeviceNetworkComponent? deviceNet))
                    continue;

                var address = MetaData(deviceUid).EntityLifeStage == EntityLifeStage.MapInitialized
                    ? deviceNet.Address
                    : $"UID: {deviceUid.ToString()}";
                devices.Add(address, deviceUid);
            }
            return devices;
        }

        public bool ExistsInDeviceList(EntityUid uid, string address, DeviceListComponent? deviceList = null)
        {
            var addresses = GetDeviceList(uid).Keys;
            return addresses.Contains(address);
        }

        private void OnBeforeBroadcast(EntityUid uid, DeviceListComponent component, BeforeBroadcastAttemptEvent args)
        {
            if (component.Devices.Count == 0)
            {
                if (component.IsAllowList)
                    args.Cancel();
                return;
            }

            HashSet<DeviceNetworkComponent> filteredRecipients = new(args.Recipients.Count);
            foreach (var recipient in args.Recipients)
            {
                if (component.Devices.Contains(recipient.Owner) == component.IsAllowList)
                    filteredRecipients.Add(recipient);
            }
            args.ModifiedRecipients = filteredRecipients;
        }

        private void OnBeforePacketSent(EntityUid uid, DeviceListComponent component, BeforePacketSentEvent args)
        {
            if (component.HandleIncomingPackets && component.Devices.Contains(args.Sender) != component.IsAllowList)
                args.Cancel();
        }

        private void OnMapSave(BeforeSaveEvent ev)
        {
            List<EntityUid> toRemove = new();
            var query = GetEntityQuery<TransformComponent>();
            var enumerator = AllEntityQuery<DeviceListComponent, TransformComponent>();
            while (enumerator.MoveNext(out var uid, out var device, out var xform))
            {
                if (xform.MapUid != ev.Map)
                    continue;

                foreach (var ent in device.Devices)
                {
                    if (!query.TryGetComponent(ent, out var linkedXform))
                    {
                        toRemove.Add(ent);
                        continue;
                    }

                    if (linkedXform.MapUid == ev.Map)
                        continue;

                    toRemove.Add(ent);
                }

                if (toRemove.Count == 0)
                    continue;

                var old = device.Devices.ToList();
                device.Devices.ExceptWith(toRemove);
                RaiseLocalEvent(uid, new DeviceListUpdateEvent(old, device.Devices.ToList()));
                Dirty(uid, device);
                toRemove.Clear();
            }
        }

        /// <summary>
        ///     Updates the device list stored on this entity.
        /// </summary>
        /// <param name="uid">The entity to update.</param>
        /// <param name="devices">The devices to store.</param>
        /// <param name="merge">Whether to merge or replace the devices stored.</param>
        /// <param name="deviceList">Device list component</param>
        public DeviceListUpdateResult UpdateDeviceList(EntityUid uid, IEnumerable<EntityUid> devices, bool merge = false, DeviceListComponent? deviceList = null)
        {
            if (!Resolve(uid, ref deviceList))
                return DeviceListUpdateResult.NoComponent;

            var list = devices.ToList();
            var newDevices = new HashSet<EntityUid>(list);

            if (merge)
                newDevices.UnionWith(deviceList.Devices);

            if (newDevices.Count > deviceList.DeviceLimit)
            {
                return DeviceListUpdateResult.TooManyDevices;
            }

            var query = GetEntityQuery<DeviceNetworkComponent>();
            var oldDevices = deviceList.Devices.ToList();
            foreach (var device in oldDevices)
            {
                if (newDevices.Contains(device))
                    continue;

                deviceList.Devices.Remove(device);
                if (query.TryGetComponent(device, out var comp))
                    comp.DeviceLists.Remove(uid);
            }

            foreach (var device in newDevices)
            {
                if (!query.TryGetComponent(device, out var comp))
                    continue;

                if (!deviceList.Devices.Add(device))
                    continue;

                comp.DeviceLists.Add(uid);
            }

            RaiseLocalEvent(uid, new DeviceListUpdateEvent(oldDevices, list));

            Dirty(uid, deviceList);

            return DeviceListUpdateResult.UpdateOk;
        }
    }
}
