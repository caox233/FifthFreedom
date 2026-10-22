using SplinterCellCNLauncher.Models;
using System;

namespace SplinterCellCNLauncher.Services;

public sealed record ServerRuntimeSnapshot(
    ServerDescriptorSnapshot DescriptorSnapshot,
    ServerConnectionProfile ConnectionProfile,
    string PublicEndpoint,
    string ServerVirtualIp,
    int ControlPlanePort,
    int DedicatedConfigPort,
    int DedicatedAuthPort,
    int DedicatedGamePort,
    string NetworkName,
    string NetworkSecret,
    int NetworkPort,
    int Mtu,
    bool EnableIpv6);

public static class ServerRuntimeConfiguration
{
    public static ServerRuntimeSnapshot Create(
        ServerDescriptorSnapshot descriptorSnapshot,
        ServerConnectionProfile connectionProfile)
    {
        ArgumentNullException.ThrowIfNull(descriptorSnapshot);
        ArgumentNullException.ThrowIfNull(connectionProfile);
        ServerDescriptor descriptor = descriptorSnapshot.Descriptor;
        string host = descriptor.Network.UseOriginHost
            || string.IsNullOrWhiteSpace(descriptor.Network.PublicHost)
                ? PublicTunnelConfig.GetEndpointHost(connectionProfile.PublicEndpoint)
                : descriptor.Network.PublicHost.Trim().Trim('[', ']');
        string endpoint = PublicTunnelConfig.BuildEndpoint(host, descriptor.Network.PublicPort);
        return new ServerRuntimeSnapshot(
            descriptorSnapshot,
            connectionProfile,
            endpoint,
            descriptor.ControlPlane.Address,
            descriptor.ControlPlane.Port,
            descriptor.Dedicated.ConfigPort,
            descriptor.Dedicated.AuthPort,
            descriptor.Dedicated.GamePort,
            descriptor.Network.NetworkName,
            descriptor.Network.Secret,
            descriptor.Network.PublicPort,
            descriptor.Network.Mtu,
            descriptor.Network.EnableIpv6);
    }
}
