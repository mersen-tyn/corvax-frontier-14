using System.Linq;
using Content.Server.DeviceNetwork.Components;
using Content.Shared.DeviceNetwork;
using Content.Shared.DeviceNetwork.Components;
using Content.Shared.DeviceNetwork.Systems;
using Content.Shared.Interaction;
using JetBrains.Annotations;
using Robust.Shared.Map.Events;

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
        SubscribeLocalEvent<RoundEndTextEvent>(OnRoundEnd);
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

    private void OnGetState(EntityUid uid, DeviceListComponent component, ref ComponentGetState args)
    {
        _processingData.Add(new DeviceListProcessingData(uid, component, args));
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

    private const string DeviceListProcessedEvent = "DeviceListProcessed";

    private void OnRoundEnd(RoundEndTextEvent ev)
    {
        RaiseLocalEvent(new GenericEvent(DeviceListProcessedEvent));
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
