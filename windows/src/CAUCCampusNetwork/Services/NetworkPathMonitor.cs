using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CAUCCampusCore;

namespace CAUCCampusNetwork.Services;

public sealed class NetworkPathMonitor : IDisposable
{
    private readonly object _lock = new();
    private string _currentSignature;
    private bool _disposed;

    public event Action<string>? PathSignatureChanged;

    public NetworkPathMonitor()
    {
        _currentSignature = ComputeCurrentSignature();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public string CurrentSignature
    {
        get
        {
            lock (_lock)
            {
                return _currentSignature;
            }
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        EvaluateChange();
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        EvaluateChange();
    }

    private void EvaluateChange()
    {
        string nextSignature = ComputeCurrentSignature();
        bool changed = false;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (!string.Equals(nextSignature, _currentSignature, StringComparison.Ordinal))
            {
                _currentSignature = nextSignature;
                changed = true;
            }
        }

        if (changed)
        {
            PathSignatureChanged?.Invoke(nextSignature);
        }
    }

    public static string ComputeCurrentSignature()
    {
        string statusPart = NetworkInterface.GetIsNetworkAvailable() ? "available" : "unavailable";
        var physicalEntries = new List<string>();
        var otherEntries = new List<string>();

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up
                    || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var addresses = ExtractAddressSummary(ni);
                if (CampusQuotaClient.IsPhysicalAdapter(ni))
                {
                    physicalEntries.Add($"{ni.Id}({addresses})");
                }
                else if (CampusQuotaClient.IsVirtualOrTunnelAdapter(ni.Name, ni.Description)
                         || ni.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp or NetworkInterfaceType.Unknown)
                {
                    otherEntries.Add($"{ni.Name}({addresses})");
                }
            }
        }
        catch
        {
            return $"{statusPart}|error";
        }

        physicalEntries.Sort(StringComparer.Ordinal);
        otherEntries.Sort(StringComparer.Ordinal);

        string phys = string.Join(",", physicalEntries.Distinct(StringComparer.Ordinal));
        string other = string.Join(",", otherEntries.Distinct(StringComparer.Ordinal));
        bool hasOther = otherEntries.Count > 0;

        return $"{statusPart}|phys:{phys}|other:{hasOther}:{other}";
    }

    private static string ExtractAddressSummary(NetworkInterface ni)
    {
        try
        {
            var ips = new List<string>();
            foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
            {
                var addr = unicast.Address;
                if (IPAddress.IsLoopback(addr))
                {
                    continue;
                }

                if (addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    ips.Add(addr.ToString());
                }
                else if (addr.AddressFamily == AddressFamily.InterNetworkV6 && !addr.IsIPv6LinkLocal)
                {
                    ips.Add(addr.ToString());
                }
            }

            ips.Sort(StringComparer.Ordinal);
            return string.Join(";", ips);
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }
}
