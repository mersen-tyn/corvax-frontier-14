using System.Linq;
using System.Collections.Generic;
using Content.Server.DeviceNetwork.Components;
using Content.Shared.DeviceNetwork;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.DeviceNetwork.Systems;
using Content.Shared.Interaction;
using JetBrains.Annotations;
using Robust.Shared.Map.Events;
using Robust.Shared.GameObjects;
using Robust.Shared.GameStates;
using Robust.Shared.Timing;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.IoC;

namespace Content.Server.DeviceNetwork.Systems;

[UsedImplicitly]
public sealed class DeviceListSystem : SharedDeviceListSystem
{
    [Dependency] private readonly NetworkConfiguratorSystem _configurator = default!;
    private readonly List<DeviceListProcessingData> _processingData = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<DeviceListComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<DeviceListComponent, BeforeBroadcastAttemptEvent>(OnBeforeBroadcast);
        SubscribeLocalEvent<DeviceListComponent, BeforePacketSentEvent>(OnBeforePacketSent);
        SubscribeLocalEvent<BeforeSaveEvent>(OnMapSave);
        SubscribeLocalEvent<PostUpdateEvent>(OnPostUpdate);
        SubscribeLocalEvent<GenericEvent>(OnDeviceListProcessed);
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
                Log.Error(
                    $"Saving a device list ({ToPrettyString(uid)}) that has a reference to an entity on another map ({ToPrettyString(ent)}). Removing entity from list.");
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

    private void OnPostUpdate(PostUpdateEvent ev)
    {
        RaiseLocalEvent(new GenericEvent(DeviceListProcessedEvent));
    }

    private void OnDeviceListProcessed(GenericEvent ev)
    {
        if (ev.Event != DeviceListProcessedEvent)
            return;

        foreach (var data in _processingData)
        {
            var component = data.Component;
            var args = data.Args;
            var entities = new HashSet<EntityUid>(component.Devices.Count);
            foreach (var device in component.Devices)
            {
                if (EntityManager.TryGetComponent(device, out MetaDataComponent? meta))
                    entities.Add(device);
            }

            args.State = new DeviceListComponentState(entities, component.IsAllowList, component.HandleIncomingPackets);
        }

        _processingData.Clear();
    }

    public sealed class DeviceListProcessingData
    {
        public EntityUid Uid { get; }
        public DeviceListComponent Component { get; }
        public ComponentGetState Args { get; }

        public DeviceListProcessingData(EntityUid uid, DeviceListComponent component, ComponentGetState args)
        {
            Uid = uid;
            Component = component;
            Args = args;
        }
    }
}
