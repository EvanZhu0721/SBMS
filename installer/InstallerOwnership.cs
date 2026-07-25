using System;
using System.Collections.Generic;

namespace SBMSSetup
{
    internal sealed class InstallerOwnershipPolicy
    {
        internal string OriginalInf;
        internal string Provider;
        internal string ClassGuid;
        internal string[] Services;
        internal string[] InstanceIdPrefixes;
        internal string[] HardwareIds;
        internal string[] ContainerIds;
        internal string[] ParentInstanceIds;
        internal string[] ParentInstancePrefixes;
        internal string[] ExpectedPackageFiles;
        internal string[] ApprovedPackageContentIdentities;
        internal string[] ApprovedSigners;
    }

    internal static class InstallerOwnership
    {
        internal static bool IsOwnedPackage(
            DriverPackageEvidence package,
            InstallerOwnershipPolicy policy)
        {
            if (package == null || policy == null)
            {
                return false;
            }
            return EqualsIgnoreCase(
                    package.OriginalInf,
                    policy.OriginalInf) &&
                String.Equals(
                    package.Provider,
                    policy.Provider,
                    StringComparison.Ordinal) &&
                EqualsIgnoreCase(
                    package.ClassGuid,
                    policy.ClassGuid) &&
                package.CatalogTrustVerified &&
                package.CatalogMembershipVerified &&
                package.TimestampVerified &&
                package.TimestampChainValid &&
                IsHex(package.SignerThumbprint, 40) &&
                IsHex(package.TimestampThumbprint, 40) &&
                IsValidTimestampProvenance(package) &&
                !String.IsNullOrWhiteSpace(
                    package.TimestampChainStatus) &&
                String.Equals(
                    package.SignatureProvenance,
                    "WinVerifyTrust+CatalogMembership+Authenticode",
                    StringComparison.Ordinal) &&
                ContainsOrdinal(
                    policy.ApprovedSigners,
                    package.Signer) &&
                ContainsIgnoreCase(
                    policy.ApprovedPackageContentIdentities,
                    package.ContentIdentity);
        }

        private static bool IsValidTimestampProvenance(
            DriverPackageEvidence package)
        {
            DateTimeOffset timestamp;
            if (!DateTimeOffset.TryParseExact(
                    package.TimestampUtc,
                    "o",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out timestamp) ||
                timestamp.Offset != TimeSpan.Zero)
            {
                return false;
            }
            return
                (String.Equals(
                    package.TimestampType,
                    "RFC3161",
                    StringComparison.Ordinal) &&
                String.Equals(
                    package.TimestampOid,
                    WindowsDriverSignatureInspector.Rfc3161Oid,
                    StringComparison.Ordinal)) ||
                (String.Equals(
                    package.TimestampType,
                    "AuthenticodeCountersignature",
                    StringComparison.Ordinal) &&
                String.Equals(
                    package.TimestampOid,
                    WindowsDriverSignatureInspector.CounterSignatureOid,
                    StringComparison.Ordinal));
        }

        internal static bool IsOwnedResidualDevice(
            DeviceInventoryEvidence device,
            InstallerOwnershipPolicy policy)
        {
            if (device == null ||
                policy == null ||
                !MatchesInstanceNamespace(
                    policy,
                    device.InstanceId) ||
                !ContainsIgnoreCase(
                    policy.ContainerIds,
                    device.ContainerId) ||
                !MatchesParent(policy, device.Parent) ||
                policy.HardwareIds == null ||
                policy.HardwareIds.Length == 0)
            {
                return false;
            }
            bool hardwareIdMatch = false;
            foreach (string allowedId in policy.HardwareIds)
            {
                if (ContainsIgnoreCase(
                    device.HardwareIds,
                    allowedId))
                {
                    hardwareIdMatch = true;
                    break;
                }
            }
            return hardwareIdMatch;
        }

        internal static bool HasHealthyOwnedBinding(
            DeviceInventoryEvidence device,
            DriverPackageEvidence package,
            InstallerOwnershipPolicy policy)
        {
            if (device == null ||
                !IsOwnedResidualDevice(device, policy) ||
                !IsOwnedPackage(package, policy) ||
                !device.Present ||
                device.HasProblem ||
                !ContainsOrdinal(
                    policy.Services,
                    device.Service) ||
                !EqualsIgnoreCase(
                    device.BindingPublishedInf,
                    package.PublishedInf) ||
                !EqualsIgnoreCase(
                    device.BindingContentIdentity,
                    package.ContentIdentity))
            {
                return false;
            }
            return true;
        }

        private static bool MatchesInstanceNamespace(
            InstallerOwnershipPolicy policy,
            string instanceId)
        {
            if (policy.InstanceIdPrefixes == null ||
                String.IsNullOrWhiteSpace(instanceId))
            {
                return false;
            }
            foreach (string prefix in policy.InstanceIdPrefixes)
            {
                if (!String.IsNullOrWhiteSpace(prefix) &&
                    instanceId.StartsWith(
                        prefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool MatchesParent(
            InstallerOwnershipPolicy policy,
            string parent)
        {
            if (String.IsNullOrWhiteSpace(parent))
            {
                return false;
            }
            if (ContainsIgnoreCase(
                policy.ParentInstanceIds,
                parent))
            {
                return true;
            }
            if (policy.ParentInstancePrefixes == null)
            {
                return false;
            }
            foreach (string prefix in policy.ParentInstancePrefixes)
            {
                if (!String.IsNullOrWhiteSpace(prefix) &&
                    parent.StartsWith(
                        prefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        internal static DriverPackageEvidence[] OwnedPackages(
            WindowsDriverInventory inventory,
            InstallerOwnershipPolicy policy)
        {
            var result = new List<DriverPackageEvidence>();
            if (inventory == null || inventory.Packages == null)
            {
                return result.ToArray();
            }
            foreach (DriverPackageEvidence package in inventory.Packages)
            {
                if (IsOwnedPackage(package, policy))
                {
                    result.Add(package);
                }
            }
            return result.ToArray();
        }

        internal static DeviceInventoryEvidence[] OwnedResidualDevices(
            WindowsDriverInventory inventory,
            InstallerOwnershipPolicy policy)
        {
            var result = new List<DeviceInventoryEvidence>();
            if (inventory == null ||
                inventory.Devices == null)
            {
                return result.ToArray();
            }
            foreach (DeviceInventoryEvidence device in inventory.Devices)
            {
                if (IsOwnedResidualDevice(device, policy))
                {
                    result.Add(device);
                }
            }
            return result.ToArray();
        }

        internal static DeviceInventoryEvidence[]
            HealthyActiveOwnedDevices(
                WindowsDriverInventory inventory,
                InstallerOwnershipPolicy policy)
        {
            var result = new List<DeviceInventoryEvidence>();
            if (inventory == null ||
                inventory.Packages == null ||
                inventory.Devices == null)
            {
                return result.ToArray();
            }
            var packages =
                new Dictionary<string, DriverPackageEvidence>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (DriverPackageEvidence package in inventory.Packages)
            {
                packages[package.PublishedInf] = package;
            }
            foreach (DeviceInventoryEvidence device in inventory.Devices)
            {
                DriverPackageEvidence package;
                if (packages.TryGetValue(
                        device.BindingPublishedInf,
                        out package) &&
                    HasHealthyOwnedBinding(device, package, policy))
                {
                    result.Add(device);
                }
            }
            return result.ToArray();
        }

        private static bool ContainsOrdinal(
            string[] values,
            string candidate)
        {
            if (values == null || String.IsNullOrEmpty(candidate))
            {
                return false;
            }
            foreach (string value in values)
            {
                if (String.Equals(
                    value,
                    candidate,
                    StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool ContainsIgnoreCase(
            string[] values,
            string candidate)
        {
            if (values == null || String.IsNullOrEmpty(candidate))
            {
                return false;
            }
            foreach (string value in values)
            {
                if (EqualsIgnoreCase(value, candidate))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool EqualsIgnoreCase(
            string left,
            string right)
        {
            return !String.IsNullOrEmpty(left) &&
                !String.IsNullOrEmpty(right) &&
                String.Equals(
                    left,
                    right,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsHex(string value, int length)
        {
            if (String.IsNullOrEmpty(value) ||
                value.Length != length)
            {
                return false;
            }
            foreach (char character in value)
            {
                bool hex =
                    (character >= '0' && character <= '9') ||
                    (character >= 'a' && character <= 'f') ||
                    (character >= 'A' && character <= 'F');
                if (!hex)
                {
                    return false;
                }
            }
            return true;
        }
    }
}
