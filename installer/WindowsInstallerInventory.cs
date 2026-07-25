using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SBMSSetup
{
    internal sealed class DriverFileEvidence
    {
        internal string Name;
        internal string Sha256;
    }

    internal sealed class DriverPackageEvidence
    {
        internal string PublishedInf;
        internal string OriginalInf;
        internal string Provider;
        internal string ClassName;
        internal string ClassGuid;
        internal string DriverDateAndVersion;
        internal string CatalogFile;
        internal string Signer;
        internal string PnpUtilSigner;
        internal string WhcpVersion;
        internal string[] CatalogAttributes;
        internal DriverFileEvidence[] Files;
        internal bool CatalogTrustVerified;
        internal bool CatalogMembershipVerified;
        internal bool TimestampVerified;
        internal string SignerThumbprint;
        internal string TimestampThumbprint;
        internal string TimestampUtc;
        internal string TimestampType;
        internal string TimestampOid;
        internal bool TimestampChainValid;
        internal string TimestampChainStatus;
        internal string SignatureProvenance;
        internal string ContentIdentity;
    }

    internal sealed class DeviceInventoryEvidence
    {
        internal string InstanceId;
        internal bool Present;
        internal string[] HardwareIds;
        internal string Service;
        internal string BindingPublishedInf;
        internal string BindingContentIdentity;
        internal string ContainerId;
        internal string Parent;
        internal uint DevNodeStatus;
        internal uint ProblemCode;

        internal bool HasProblem
        {
            get { return ProblemCode != 0; }
        }
    }

    internal sealed class WindowsDriverInventory
    {
        internal DriverPackageEvidence[] Packages;
        internal DeviceInventoryEvidence[] Devices;
        internal string EvidenceDigest;
    }

    internal sealed class PnpUtilDriverRecord
    {
        internal string PublishedInf;
        internal string OriginalInf;
        internal string Provider;
        internal string ClassName;
        internal string ClassGuid;
        internal string DriverDateAndVersion;
        internal string CatalogFile;
        internal string Signer;
        internal string WhcpVersion;
        internal string[] CatalogAttributes;
        internal string[] Files;
        internal string[] DeviceInstanceIds;
    }

    internal sealed class PnpUtilDeviceRecord
    {
        internal string InstanceId;
        internal string ClassGuid;
        internal string ActivePublishedInf;
        internal string[] HardwareIds;
        internal string Parent;
        internal string Service;
    }

    internal static class PnpUtilXmlParser
    {
        private static readonly Regex PublishedInfPattern =
            new Regex(
                "^oem[0-9]+[.]inf$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static PnpUtilDriverRecord[] Parse(string xml)
        {
            if (String.IsNullOrWhiteSpace(xml))
            {
                throw new InvalidOperationException(
                    "PnPUtil XML inventory is empty.");
            }

            XmlDocument document = Load(xml);

            var records = new List<PnpUtilDriverRecord>();
            var publishedNames =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode node in document.DocumentElement.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element ||
                    !String.Equals(
                        node.LocalName,
                        "Driver",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                string published = Attribute(node, "DriverName");
                if (!PublishedInfPattern.IsMatch(published))
                {
                    throw new InvalidOperationException(
                        "PnPUtil returned an invalid published INF locator.");
                }
                if (!publishedNames.Add(published))
                {
                    throw new InvalidOperationException(
                        "PnPUtil returned a duplicate published INF locator.");
                }

                var files = new SortedSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                XmlNode filesNode = Child(node, "Files", false);
                if (filesNode != null)
                {
                    foreach (XmlNode fileNode in filesNode.ChildNodes)
                    {
                        if (fileNode.NodeType != XmlNodeType.Element ||
                            !String.Equals(
                                fileNode.LocalName,
                                "File",
                                StringComparison.Ordinal))
                        {
                            continue;
                        }
                        string name = Attribute(fileNode, "Name");
                        files.Add(ValidateRelativeFilePath(
                            name,
                            "driver file"));
                    }
                }

                var devices = new SortedSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                XmlNode devicesNode = Child(node, "Devices", false);
                if (devicesNode != null)
                {
                    foreach (XmlNode deviceNode in devicesNode.ChildNodes)
                    {
                        if (deviceNode.NodeType != XmlNodeType.Element ||
                            !String.Equals(
                                deviceNode.LocalName,
                                "Device",
                                StringComparison.Ordinal))
                        {
                            continue;
                        }
                        string instanceId = Attribute(
                            deviceNode,
                            "InstanceId");
                        Require(instanceId, "device instance ID");
                        devices.Add(instanceId);
                        // Deliberately ignore Device/Status. It is localized
                        // presentation text, not machine-readable evidence.
                    }
                }

                var attributes = new SortedSet<string>(
                    StringComparer.Ordinal);
                XmlNode attributesNode =
                    Child(node, "CatalogAttributes", false);
                if (attributesNode != null)
                {
                    foreach (XmlNode attributeNode in
                        attributesNode.ChildNodes)
                    {
                        if (attributeNode.NodeType == XmlNodeType.Element &&
                            String.Equals(
                                attributeNode.LocalName,
                                "Attribute",
                                StringComparison.Ordinal))
                        {
                            string value = attributeNode.InnerText.Trim();
                            Require(value, "catalog attribute");
                            attributes.Add(value);
                        }
                    }
                }

                string originalInf = Text(node, "OriginalName", true);
                string catalogFile = Text(node, "CatalogFile", true);
                originalInf = ValidateLeafName(
                    originalInf,
                    "original INF");
                catalogFile = ValidateLeafName(
                    catalogFile,
                    "catalog file");
                records.Add(new PnpUtilDriverRecord
                {
                    PublishedInf = published,
                    OriginalInf = originalInf,
                    Provider = Text(node, "ProviderName", true),
                    ClassName = Text(node, "ClassName", true),
                    ClassGuid = NormalizeGuid(
                        Text(node, "ClassGuid", true)),
                    DriverDateAndVersion =
                        Text(node, "DriverVersion", true),
                    CatalogFile = catalogFile,
                    Signer = Text(node, "SignerName", true),
                    WhcpVersion = Text(node, "WhcpVersion", false),
                    CatalogAttributes = ToArray(attributes),
                    Files = ToArray(files),
                    DeviceInstanceIds = ToArray(devices)
                });
            }
            return records.ToArray();
        }

        internal static PnpUtilDeviceRecord[] ParseDevices(string xml)
        {
            XmlDocument document = Load(xml);
            var records = new List<PnpUtilDeviceRecord>();
            var instanceIds =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XmlNode node in document.DocumentElement.ChildNodes)
            {
                if (node.NodeType != XmlNodeType.Element ||
                    !String.Equals(
                        node.LocalName,
                        "Device",
                        StringComparison.Ordinal))
                {
                    continue;
                }
                string instanceId = Attribute(node, "InstanceId");
                if (!instanceIds.Add(instanceId))
                {
                    throw new InvalidOperationException(
                        "PnPUtil returned a duplicate device instance ID.");
                }
                var hardwareIds = new SortedSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                XmlNode hardwareIdsNode =
                    Child(node, "HardwareIds", false);
                if (hardwareIdsNode != null)
                {
                    foreach (XmlNode idNode in
                        hardwareIdsNode.ChildNodes)
                    {
                        if (idNode.NodeType == XmlNodeType.Element &&
                            String.Equals(
                                idNode.LocalName,
                                "HardwareId",
                                StringComparison.Ordinal))
                        {
                            string id = idNode.InnerText.Trim();
                            Require(id, "hardware ID");
                            hardwareIds.Add(id);
                        }
                    }
                }
                string activeInf = Text(
                    node,
                    "DriverName",
                    false);
                if (!String.IsNullOrWhiteSpace(activeInf) &&
                    (!String.Equals(
                        Path.GetFileName(activeInf),
                        activeInf,
                        StringComparison.Ordinal) ||
                     !activeInf.EndsWith(
                        ".inf",
                        StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        "PnPUtil device active INF locator is invalid.");
                }
                string classGuid = Text(node, "ClassGuid", false);
                records.Add(new PnpUtilDeviceRecord
                {
                    InstanceId = instanceId,
                    ClassGuid = NormalizeOptionalGuid(classGuid),
                    ActivePublishedInf = activeInf,
                    HardwareIds = ToArray(hardwareIds),
                    Parent = Text(node, "Parent", false),
                    Service = Text(node, "Service", false)
                });
                // Deliberately ignore Device/Status and matching-driver
                // Status. Presence/problem are read from cfgmgr32.
            }
            return records.ToArray();
        }

        private static XmlDocument Load(string xml)
        {
            if (String.IsNullOrWhiteSpace(xml))
            {
                throw new InvalidOperationException(
                    "PnPUtil XML inventory is empty.");
            }
            var settings = new XmlReaderSettings();
            settings.DtdProcessing = DtdProcessing.Prohibit;
            settings.XmlResolver = null;
            var document = new XmlDocument();
            document.XmlResolver = null;
            using (var stringReader = new StringReader(xml))
            using (XmlReader reader = XmlReader.Create(
                stringReader,
                settings))
            {
                document.Load(reader);
            }
            if (document.DocumentElement == null ||
                !String.Equals(
                    document.DocumentElement.LocalName,
                    "PnpUtil",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "PnPUtil XML root is not recognized.");
            }
            return document;
        }

        private static XmlNode Child(
            XmlNode parent,
            string name,
            bool required)
        {
            foreach (XmlNode child in parent.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element &&
                    String.Equals(
                        child.LocalName,
                        name,
                        StringComparison.Ordinal))
                {
                    return child;
                }
            }
            if (required)
            {
                throw new InvalidOperationException(
                    "PnPUtil XML is missing " + name + ".");
            }
            return null;
        }

        private static string Text(
            XmlNode parent,
            string name,
            bool required)
        {
            XmlNode child = Child(parent, name, required);
            string value = child == null ? String.Empty :
                child.InnerText.Trim();
            if (required)
            {
                Require(value, name);
            }
            return value;
        }

        private static string Attribute(XmlNode node, string name)
        {
            XmlAttribute attribute = node.Attributes == null ?
                null : node.Attributes[name];
            string value = attribute == null ?
                String.Empty : attribute.Value.Trim();
            Require(value, name);
            return value;
        }

        private static string NormalizeGuid(string value)
        {
            Guid parsed;
            if (!Guid.TryParse(value, out parsed))
            {
                throw new InvalidOperationException(
                    "PnPUtil class GUID is invalid.");
            }
            return parsed.ToString("B").ToUpperInvariant();
        }

        private static string NormalizeOptionalGuid(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return String.Empty;
            }
            Guid parsed;
            if (!Guid.TryParse(value, out parsed))
            {
                // Some orphan or pseudo devices expose an unavailable
                // presentation value. Candidate ownership never trusts this
                // field; retain it only as evidence.
                return value.Trim();
            }
            return parsed.ToString("B").ToUpperInvariant();
        }

        private static string ValidateLeafName(
            string value,
            string label)
        {
            Require(value, label);
            if (!String.Equals(
                    Path.GetFileName(value),
                    value,
                    StringComparison.Ordinal) ||
                value.IndexOfAny(new[]
                {
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                }) >= 0)
            {
                throw new InvalidOperationException(
                    "PnPUtil " + label + " is not a leaf name.");
            }
            return value;
        }

        private static string ValidateRelativeFilePath(
            string value,
            string label)
        {
            Require(value, label);
            string normalized = value.Replace(
                Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalized))
            {
                throw new InvalidOperationException(
                    "PnPUtil " + label + " is not a safe relative path.");
            }
            string[] segments = normalized.Split(
                Path.DirectorySeparatorChar);
            foreach (string segment in segments)
            {
                if (String.IsNullOrWhiteSpace(segment) ||
                    segment == "." ||
                    segment == "..")
                {
                    throw new InvalidOperationException(
                        "PnPUtil " + label +
                        " is not a safe relative path.");
                }
            }
            return normalized;
        }

        private static void Require(string value, string label)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    "PnPUtil XML " + label + " is missing.");
            }
        }

        private static string[] ToArray(SortedSet<string> values)
        {
            var result = new string[values.Count];
            values.CopyTo(result);
            return result;
        }
    }

    internal interface IWindowsInventoryProvider
    {
        WindowsDriverInventory Inspect(
            InstallerOwnershipPolicy candidatePolicy);
    }

    internal sealed class BoundedProcessResult
    {
        internal int ExitCode;
        internal string StandardOutput;
        internal string StandardError;
    }

    internal static class BoundedReadOnlyProcess
    {
        internal static BoundedProcessResult Run(
            ProcessStartInfo start,
            int timeoutMilliseconds,
            string label)
        {
            using (Process process = Process.Start(start))
            {
                if (process == null)
                {
                    throw new InvalidOperationException(
                        "Unable to start " + label + ".");
                }
                var outputTask =
                    process.StandardOutput.ReadToEndAsync();
                var errorTask =
                    process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(timeoutMilliseconds))
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(5000);
                    }
                    catch
                    {
                    }
                    throw new TimeoutException(
                        label + " exceeded its read-only timeout.");
                }
                return new BoundedProcessResult
                {
                    ExitCode = process.ExitCode,
                    StandardOutput =
                        outputTask.GetAwaiter().GetResult(),
                    StandardError =
                        errorTask.GetAwaiter().GetResult()
                };
            }
        }
    }

    internal sealed class ReadOnlyPathLease : IDisposable
    {
        private const uint GenericRead = 0x80000000;
        private const uint FileListDirectory = 0x00000001;
        private const uint FileReadAttributes = 0x00000080;
        private const uint FileShareRead = 0x00000001;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const int FileAttributeTagInfoClass = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct FileAttributeTagInfo
        {
            internal uint FileAttributes;
            internal uint ReparseTag;
        }

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(
            SafeFileHandle file,
            int fileInformationClass,
            out FileAttributeTagInfo fileInformation,
            uint bufferSize);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder filePath,
            uint filePathSize,
            uint flags);

        private readonly SafeFileHandle handle;
        internal readonly string RequestedPath;
        internal readonly string FinalPath;
        internal readonly bool IsDirectory;

        private ReadOnlyPathLease(
            string path,
            bool directory,
            string requiredRootFinalPath)
        {
            RequestedPath = Path.GetFullPath(path);
            IsDirectory = directory;
            uint access = directory ?
                FileListDirectory | FileReadAttributes :
                GenericRead | FileReadAttributes;
            uint flags = FileFlagOpenReparsePoint |
                (directory ? FileFlagBackupSemantics : 0);
            handle = CreateFile(
                RequestedPath,
                access,
                FileShareRead,
                IntPtr.Zero,
                OpenExisting,
                flags,
                IntPtr.Zero);
            if (handle == null || handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                if (handle != null)
                {
                    handle.Dispose();
                }
                throw new Win32Exception(
                    error,
                    "Unable to lease read-only audit path: " +
                    RequestedPath);
            }
            try
            {
                FileAttributeTagInfo tag;
                if (!GetFileInformationByHandleEx(
                    handle,
                    FileAttributeTagInfoClass,
                    out tag,
                    (uint)Marshal.SizeOf(
                        typeof(FileAttributeTagInfo))))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Unable to inspect audit path handle.");
                }
                if ((tag.FileAttributes &
                    (uint)FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Audit path handle is a reparse point: " +
                        RequestedPath);
                }
                bool actualDirectory =
                    (tag.FileAttributes &
                    (uint)FileAttributes.Directory) != 0;
                if (actualDirectory != directory)
                {
                    throw new InvalidOperationException(
                        "Audit path type changed while opening: " +
                        RequestedPath);
                }
                FinalPath = ReadFinalPath(handle);
                if (!String.IsNullOrWhiteSpace(
                        requiredRootFinalPath) &&
                    !IsWithin(
                        requiredRootFinalPath,
                        FinalPath))
                {
                    throw new InvalidOperationException(
                        "Audit handle escaped its leased root: " +
                        RequestedPath);
                }
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        internal static ReadOnlyPathLease OpenRootDirectory(
            string path)
        {
            return new ReadOnlyPathLease(path, true, null);
        }

        internal static ReadOnlyPathLease OpenDirectory(
            string path,
            ReadOnlyPathLease root)
        {
            return new ReadOnlyPathLease(
                path,
                true,
                root.FinalPath);
        }

        internal static ReadOnlyPathLease OpenFile(
            string path,
            ReadOnlyPathLease root)
        {
            return new ReadOnlyPathLease(
                path,
                false,
                root.FinalPath);
        }

        internal FileStream OpenReadStream()
        {
            if (IsDirectory)
            {
                throw new InvalidOperationException(
                    "A directory lease cannot be read as a file.");
            }
            var borrowed = new SafeFileHandle(
                handle.DangerousGetHandle(),
                false);
            return new FileStream(
                borrowed,
                FileAccess.Read,
                4096,
                false);
        }

        public void Dispose()
        {
            handle.Dispose();
        }

        private static string ReadFinalPath(SafeFileHandle file)
        {
            uint required = GetFinalPathNameByHandle(
                file,
                null,
                0,
                0);
            if (required == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to size final audit path.");
            }
            var buffer = new StringBuilder((int)required + 1);
            uint written = GetFinalPathNameByHandle(
                file,
                buffer,
                (uint)buffer.Capacity,
                0);
            if (written == 0 || written >= buffer.Capacity)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Unable to resolve final audit path.");
            }
            return NormalizeFinalPath(buffer.ToString());
        }

        private static string NormalizeFinalPath(string path)
        {
            const string uncPrefix = @"\\?\UNC\";
            const string localPrefix = @"\\?\";
            if (path.StartsWith(
                uncPrefix,
                StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + path.Substring(uncPrefix.Length);
            }
            if (path.StartsWith(
                localPrefix,
                StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring(localPrefix.Length);
            }
            return path;
        }

        private static bool IsWithin(string root, string candidate)
        {
            string normalizedRoot = root.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (String.Equals(
                normalizedRoot,
                candidate.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return candidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class ReadOnlyLeaseSet : IDisposable
    {
        private readonly List<ReadOnlyPathLease> leases =
            new List<ReadOnlyPathLease>();

        internal ReadOnlyPathLease Add(ReadOnlyPathLease lease)
        {
            leases.Add(lease);
            return lease;
        }

        public void Dispose()
        {
            for (int index = leases.Count - 1;
                index >= 0;
                --index)
            {
                leases[index].Dispose();
            }
        }
    }

    internal sealed class DeviceSystemEvidence
    {
        internal bool Present;
        internal string[] HardwareIds;
        internal string Service;
        internal string ActivePublishedInf;
        internal string ContainerId;
        internal string Parent;
        internal uint DevNodeStatus;
        internal uint ProblemCode;
    }

    internal interface IDeviceSystemEvidenceReader
    {
        DeviceSystemEvidence Read(string instanceId);
    }

    internal sealed class WindowsDeviceSystemEvidenceReader :
        IDeviceSystemEvidenceReader
    {
        public DeviceSystemEvidence Read(string instanceId)
        {
            string keyPath =
                @"SYSTEM\CurrentControlSet\Enum\" + instanceId;
            using (RegistryKey key =
                Registry.LocalMachine.OpenSubKey(keyPath, false))
            {
                if (key == null)
                {
                    throw new InvalidOperationException(
                        "PnP device registry evidence disappeared: " +
                        instanceId);
                }
                string activeInf = String.Empty;
                string driverClassKey = ReadString(key, "Driver");
                if (!String.IsNullOrWhiteSpace(driverClassKey))
                {
                    using (RegistryKey classKey =
                        Registry.LocalMachine.OpenSubKey(
                            @"SYSTEM\CurrentControlSet\Control\Class\" +
                            driverClassKey,
                            false))
                    {
                        if (classKey == null)
                        {
                            throw new InvalidOperationException(
                                "PnP active driver class key disappeared: " +
                                driverClassKey);
                        }
                        activeInf = ReadString(classKey, "InfPath");
                    }
                }
                uint status;
                uint problem;
                string parent;
                WindowsInventoryNative.ReadDevNodeEvidence(
                    instanceId,
                    out status,
                    out problem,
                    out parent);
                return new DeviceSystemEvidence
                {
                    Present =
                        (status & WindowsInventoryNative.DnPresent) != 0,
                    HardwareIds = ReadMultiString(key, "HardwareID"),
                    Service = ReadString(key, "Service"),
                    ActivePublishedInf = activeInf,
                    ContainerId = ReadString(key, "ContainerID"),
                    Parent = parent,
                    DevNodeStatus = status,
                    ProblemCode = problem
                };
            }
        }

        private static string[] ReadMultiString(
            RegistryKey key,
            string name)
        {
            string[] value = key.GetValue(
                name,
                new string[0],
                RegistryValueOptions.DoNotExpandEnvironmentNames)
                as string[];
            return value ?? new string[0];
        }

        private static string ReadString(
            RegistryKey key,
            string name)
        {
            object value = key.GetValue(
                name,
                String.Empty,
                RegistryValueOptions.DoNotExpandEnvironmentNames);
            return Convert.ToString(
                value,
                CultureInfo.InvariantCulture) ?? String.Empty;
        }
    }

    internal sealed class WindowsInventoryProvider :
        IWindowsInventoryProvider
    {
        private readonly string windowsDirectory;
        private readonly string pnpUtilPath;
        private readonly IDeviceSystemEvidenceReader deviceReader;
        private readonly IDriverSignatureInspector signatureInspector;

        internal WindowsInventoryProvider()
            : this(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.Windows),
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.System),
                    "pnputil.exe"),
                new WindowsDeviceSystemEvidenceReader(),
                new WindowsDriverSignatureInspector())
        {
        }

        internal WindowsInventoryProvider(
            string windowsDirectory,
            string pnpUtilPath,
            IDeviceSystemEvidenceReader deviceReader,
            IDriverSignatureInspector signatureInspector)
        {
            this.windowsDirectory = Path.GetFullPath(windowsDirectory);
            this.pnpUtilPath = Path.GetFullPath(pnpUtilPath);
            if (deviceReader == null)
            {
                throw new ArgumentNullException("deviceReader");
            }
            this.deviceReader = deviceReader;
            if (signatureInspector == null)
            {
                throw new ArgumentNullException(
                    "signatureInspector");
            }
            this.signatureInspector = signatureInspector;
        }

        public WindowsDriverInventory Inspect(
            InstallerOwnershipPolicy candidatePolicy)
        {
            if (candidatePolicy == null)
            {
                throw new ArgumentNullException("candidatePolicy");
            }
            string xml = RunInventory(
                "/enum-drivers /files /format xml");
            PnpUtilDriverRecord[] records =
                PnpUtilXmlParser.Parse(xml);
            string deviceXml = RunInventory(
                "/enum-devices /deviceids /relations /drivers " +
                "/services /format xml");
            PnpUtilDeviceRecord[] deviceRecords =
                PnpUtilXmlParser.ParseDevices(deviceXml);
            var packages = new List<DriverPackageEvidence>();
            var devices = new List<DeviceInventoryEvidence>();
            var packageByPublished =
                new Dictionary<string, DriverPackageEvidence>(
                    StringComparer.OrdinalIgnoreCase);

            foreach (PnpUtilDriverRecord record in records)
            {
                if (!IsPackageCandidate(
                    record,
                    candidatePolicy))
                {
                    continue;
                }
                DriverPackageEvidence package = BuildPackage(
                    record,
                    candidatePolicy);
                packages.Add(package);
                packageByPublished.Add(record.PublishedInf, package);
            }
            foreach (PnpUtilDeviceRecord record in deviceRecords)
            {
                if (!IsDeviceCandidate(
                    record,
                    candidatePolicy))
                {
                    continue;
                }
                devices.Add(BuildDeviceEvidence(
                    record,
                    packageByPublished,
                    deviceReader.Read(record.InstanceId)));
            }

            packages.Sort(delegate(
                DriverPackageEvidence left,
                DriverPackageEvidence right)
            {
                return StringComparer.OrdinalIgnoreCase.Compare(
                    left.PublishedInf,
                    right.PublishedInf);
            });
            devices.Sort(delegate(
                DeviceInventoryEvidence left,
                DeviceInventoryEvidence right)
            {
                return StringComparer.OrdinalIgnoreCase.Compare(
                    left.InstanceId,
                    right.InstanceId);
            });

            var inventory = new WindowsDriverInventory
            {
                Packages = packages.ToArray(),
                Devices = devices.ToArray()
            };
            inventory.EvidenceDigest = DigestInventory(inventory);
            return inventory;
        }

        private string RunInventory(string arguments)
        {
            var start = new ProcessStartInfo();
            start.FileName = pnpUtilPath;
            start.Arguments = arguments;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            BoundedProcessResult result =
                BoundedReadOnlyProcess.Run(
                    start,
                    30000,
                    "PnPUtil inventory");
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "PnPUtil read-only inventory failed with exit code " +
                    result.ExitCode.ToString(
                        CultureInfo.InvariantCulture) +
                    ": " + result.StandardError.Trim());
            }
            return result.StandardOutput;
        }

        private DriverPackageEvidence BuildPackage(
            PnpUtilDriverRecord record,
            InstallerOwnershipPolicy policy)
        {
            string publishedPath = Path.Combine(
                windowsDirectory,
                "INF",
                record.PublishedInf);
            string storeInf =
                WindowsInventoryNative.ResolveDriverStoreInf(publishedPath);
            string storeRoot = Path.GetDirectoryName(storeInf);
            if (String.IsNullOrWhiteSpace(storeRoot) ||
                !Directory.Exists(storeRoot))
            {
                throw new InvalidOperationException(
                    "Driver Store package directory is unavailable for " +
                    record.PublishedInf + ".");
            }

            var names = new SortedSet<string>(
                record.Files,
                StringComparer.OrdinalIgnoreCase);
            names.Add(record.OriginalInf);
            names.Add(record.CatalogFile);
            RequireExpectedPackageFiles(names, policy);
            using (var leases = new ReadOnlyLeaseSet())
            {
                ReadOnlyPathLease rootLease = leases.Add(
                    ReadOnlyPathLease.OpenRootDirectory(storeRoot));
                Dictionary<string, ReadOnlyPathLease> allFiles =
                    EnumerateFilesNoReparse(rootLease, leases);
                var evidence = new List<DriverFileEvidence>();
                foreach (string name in names)
                {
                    ReadOnlyPathLease match = FindPackageFile(
                        allFiles,
                        name,
                        storeRoot);
                    evidence.Add(new DriverFileEvidence
                    {
                        Name = name,
                        Sha256 = Sha256Lease(match)
                    });
                }
                evidence.Sort(delegate(
                    DriverFileEvidence left,
                    DriverFileEvidence right)
                {
                    return StringComparer.OrdinalIgnoreCase.Compare(
                        left.Name,
                        right.Name);
                });

                var package = new DriverPackageEvidence
                {
                    PublishedInf = record.PublishedInf,
                    OriginalInf = record.OriginalInf,
                    Provider = record.Provider,
                    ClassName = record.ClassName,
                    ClassGuid = record.ClassGuid,
                    DriverDateAndVersion =
                        record.DriverDateAndVersion,
                    CatalogFile = record.CatalogFile,
                    PnpUtilSigner = record.Signer,
                    WhcpVersion = record.WhcpVersion,
                    CatalogAttributes = record.CatalogAttributes,
                    Files = evidence.ToArray()
                };
                ReadOnlyPathLease catalogLease = FindPackageFile(
                    allFiles,
                    record.CatalogFile,
                    storeRoot);
                var members = new List<string>();
                foreach (DriverFileEvidence file in package.Files)
                {
                    if (!String.Equals(
                        file.Name,
                        record.CatalogFile,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        members.Add(FindPackageFile(
                            allFiles,
                            file.Name,
                            storeRoot).RequestedPath);
                    }
                }
                DriverCatalogVerifier.VerifyPackageOrThrow(
                    catalogLease.RequestedPath,
                    members.ToArray());
                package.CatalogTrustVerified = true;
                package.CatalogMembershipVerified = true;
                DriverSignatureEvidence signature =
                    signatureInspector.Inspect(
                        catalogLease.RequestedPath);
                ApplySignatureEvidence(package, signature);
                package.ContentIdentity = DigestPackage(package);
                return package;
            }
        }

        internal static void ApplySignatureEvidence(
            DriverPackageEvidence package,
            DriverSignatureEvidence signature)
        {
            if (package == null || signature == null)
            {
                throw new ArgumentNullException(
                    "Driver signature evidence is incomplete.");
            }
            DateTimeOffset timestamp;
            bool timestampKindValid =
                (signature.TimestampType == "RFC3161" &&
                    signature.TimestampOid ==
                        WindowsDriverSignatureInspector.Rfc3161Oid) ||
                (signature.TimestampType ==
                    "AuthenticodeCountersignature" &&
                    signature.TimestampOid ==
                        WindowsDriverSignatureInspector
                            .CounterSignatureOid);
            if (!signature.Valid ||
                !signature.TimestampValid ||
                !signature.TimestampChainValid ||
                String.IsNullOrWhiteSpace(signature.SignerSubject) ||
                !WindowsDriverSignatureInspector.IsCertificateThumbprint(
                    signature.SignerThumbprint) ||
                !WindowsDriverSignatureInspector.IsCertificateThumbprint(
                    signature.TimestampThumbprint) ||
                !DateTimeOffset.TryParseExact(
                    signature.TimestampUtc,
                    "o",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out timestamp) ||
                timestamp.Offset != TimeSpan.Zero ||
                !timestampKindValid ||
                String.IsNullOrWhiteSpace(
                    signature.TimestampChainStatus))
            {
                throw new InvalidOperationException(
                    "Catalog Authenticode signature or timestamp " +
                    "evidence is incomplete.");
            }
            package.Signer = signature.SignerSubject;
            package.SignerThumbprint =
                signature.SignerThumbprint.ToUpperInvariant();
            package.TimestampThumbprint =
                signature.TimestampThumbprint.ToUpperInvariant();
            package.TimestampUtc = timestamp.ToUniversalTime().ToString(
                "o",
                CultureInfo.InvariantCulture);
            package.TimestampType = signature.TimestampType;
            package.TimestampOid = signature.TimestampOid;
            package.TimestampChainValid = true;
            package.TimestampChainStatus =
                signature.TimestampChainStatus;
            package.TimestampVerified = true;
            package.SignatureProvenance =
                "WinVerifyTrust+CatalogMembership+Authenticode";
        }

        internal static void RequireExpectedPackageFiles(
            SortedSet<string> actual,
            InstallerOwnershipPolicy policy)
        {
            if (policy.ExpectedPackageFiles == null ||
                policy.ExpectedPackageFiles.Length != 3)
            {
                throw new InvalidOperationException(
                    "Production package policy must name exactly INF, DLL and CAT.");
            }
            var expected = new SortedSet<string>(
                policy.ExpectedPackageFiles,
                StringComparer.OrdinalIgnoreCase);
            if (expected.Count != 3 ||
                !actual.SetEquals(expected))
            {
                throw new InvalidOperationException(
                    "PnPUtil /files evidence does not match the complete " +
                    "production INF/DLL/CAT package set.");
            }
        }

        internal static DeviceInventoryEvidence BuildDeviceEvidence(
            PnpUtilDeviceRecord record,
            Dictionary<string, DriverPackageEvidence>
                packageByPublished,
            DeviceSystemEvidence system)
        {
            if (record == null ||
                packageByPublished == null ||
                system == null)
            {
                throw new ArgumentNullException(
                    "Device evidence inputs are incomplete.");
            }
            RequireEqualSet(
                record.InstanceId,
                "hardware IDs",
                record.HardwareIds,
                system.HardwareIds);
            RequireEqual(
                record.InstanceId,
                "service",
                record.Service,
                system.Service);
            RequireEqual(
                record.InstanceId,
                "parent",
                record.Parent,
                system.Parent);
            RequireActiveBindingAgreement(
                record.InstanceId,
                record.ActivePublishedInf,
                system.ActivePublishedInf);
            DriverPackageEvidence boundPackage = null;
            if (!String.IsNullOrWhiteSpace(
                system.ActivePublishedInf))
            {
                packageByPublished.TryGetValue(
                    system.ActivePublishedInf,
                    out boundPackage);
            }
            return new DeviceInventoryEvidence
            {
                InstanceId = record.InstanceId,
                Present = system.Present,
                HardwareIds = (string[])system.HardwareIds.Clone(),
                Service = system.Service,
                BindingPublishedInf = system.ActivePublishedInf,
                BindingContentIdentity = boundPackage == null ?
                    String.Empty :
                    boundPackage.ContentIdentity,
                ContainerId = system.ContainerId,
                Parent = system.Parent,
                DevNodeStatus = system.DevNodeStatus,
                ProblemCode = system.ProblemCode
            };
        }

        internal static void RequireActiveBindingAgreement(
            string instanceId,
            string pnpUtilInf,
            string registryInf)
        {
            if (!String.Equals(
                pnpUtilInf ?? String.Empty,
                registryInf ?? String.Empty,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "PnPUtil device inventory disagrees with the " +
                    "active device InfPath for " + instanceId + ".");
            }
        }

        private static void RequireEqual(
            string instanceId,
            string label,
            string pnpUtilValue,
            string systemValue)
        {
            if (!String.Equals(
                pnpUtilValue ?? String.Empty,
                systemValue ?? String.Empty,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "PnPUtil " + label +
                    " disagrees with independent system evidence for " +
                    instanceId + ".");
            }
        }

        private static void RequireEqualSet(
            string instanceId,
            string label,
            string[] pnpUtilValues,
            string[] systemValues)
        {
            var left = new SortedSet<string>(
                pnpUtilValues ?? new string[0],
                StringComparer.OrdinalIgnoreCase);
            var right = new SortedSet<string>(
                systemValues ?? new string[0],
                StringComparer.OrdinalIgnoreCase);
            if (!left.SetEquals(right))
            {
                throw new InvalidOperationException(
                    "PnPUtil " + label +
                    " disagree with independent system evidence for " +
                    instanceId + ".");
            }
        }

        private static bool IsPackageCandidate(
            PnpUtilDriverRecord record,
            InstallerOwnershipPolicy policy)
        {
            return String.Equals(
                    record.OriginalInf,
                    policy.OriginalInf,
                    StringComparison.OrdinalIgnoreCase) &&
                String.Equals(
                    record.Provider,
                    policy.Provider,
                    StringComparison.Ordinal) &&
                String.Equals(
                    record.ClassGuid,
                    policy.ClassGuid,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDeviceCandidate(
            PnpUtilDeviceRecord record,
            InstallerOwnershipPolicy policy)
        {
            bool namespaceMatch = false;
            if (policy.InstanceIdPrefixes != null)
            {
                foreach (string prefix in policy.InstanceIdPrefixes)
                {
                    if (!String.IsNullOrWhiteSpace(prefix) &&
                        record.InstanceId.StartsWith(
                            prefix,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        namespaceMatch = true;
                        break;
                    }
                }
            }
            if (!namespaceMatch || policy.HardwareIds == null)
            {
                return false;
            }
            foreach (string expected in policy.HardwareIds)
            {
                foreach (string actual in record.HardwareIds)
                {
                    if (String.Equals(
                        expected,
                        actual,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static ReadOnlyPathLease FindPackageFile(
            Dictionary<string, ReadOnlyPathLease> paths,
            string name,
            string root)
        {
            string normalized = name.Replace(
                Path.AltDirectorySeparatorChar,
                Path.DirectorySeparatorChar);
            string candidate = Path.GetFullPath(
                Path.Combine(root, normalized));
            string prefix = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Driver Store file escaped its package root: " +
                    name);
            }
            ReadOnlyPathLease match;
            if (!paths.TryGetValue(candidate, out match))
            {
                throw new InvalidOperationException(
                    "Driver Store file is missing in " + root +
                    ": " + name);
            }
            return match;
        }

        private static Dictionary<string, ReadOnlyPathLease>
            EnumerateFilesNoReparse(
                ReadOnlyPathLease root,
                ReadOnlyLeaseSet leases)
        {
            var files =
                new Dictionary<string, ReadOnlyPathLease>(
                    StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<ReadOnlyPathLease>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                ReadOnlyPathLease current = pending.Pop();
                string[] entries = Directory.GetFileSystemEntries(
                    current.RequestedPath,
                    "*",
                    SearchOption.TopDirectoryOnly);
                foreach (string entry in entries)
                {
                    FileAttributes attributes =
                        File.GetAttributes(entry);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        ReadOnlyPathLease directory = leases.Add(
                            ReadOnlyPathLease.OpenDirectory(
                                entry,
                                root));
                        pending.Push(directory);
                    }
                    else
                    {
                        ReadOnlyPathLease file = leases.Add(
                            ReadOnlyPathLease.OpenFile(entry, root));
                        files.Add(
                            Path.GetFullPath(entry),
                            file);
                    }
                }
            }
            return files;
        }

        private static string DigestPackage(
            DriverPackageEvidence package)
        {
            var lines = new List<string>();
            lines.Add(package.OriginalInf.ToUpperInvariant());
            lines.Add(package.Provider);
            lines.Add(package.ClassName);
            lines.Add(package.ClassGuid);
            lines.Add(package.DriverDateAndVersion);
            lines.Add(package.CatalogFile.ToUpperInvariant());
            lines.Add(package.Signer);
            lines.Add(package.PnpUtilSigner);
            lines.Add(package.SignerThumbprint);
            lines.Add(package.TimestampThumbprint);
            lines.Add(package.TimestampUtc);
            lines.Add(package.TimestampType);
            lines.Add(package.TimestampOid);
            lines.Add(package.TimestampChainStatus);
            lines.Add(package.SignatureProvenance);
            lines.Add(package.CatalogTrustVerified.ToString(
                CultureInfo.InvariantCulture));
            lines.Add(package.CatalogMembershipVerified.ToString(
                CultureInfo.InvariantCulture));
            lines.Add(package.TimestampVerified.ToString(
                CultureInfo.InvariantCulture));
            lines.Add(package.TimestampChainValid.ToString(
                CultureInfo.InvariantCulture));
            lines.Add(package.WhcpVersion);
            lines.AddRange(package.CatalogAttributes);
            foreach (DriverFileEvidence file in package.Files)
            {
                lines.Add(
                    file.Name.ToUpperInvariant() + "=" + file.Sha256);
            }
            return Sha256Text(String.Join("\n", lines.ToArray()));
        }

        private static string DigestInventory(
            WindowsDriverInventory inventory)
        {
            var lines = new List<string>();
            foreach (DriverPackageEvidence package in inventory.Packages)
            {
                lines.Add(
                    "P|" + package.PublishedInf.ToUpperInvariant() +
                    "|" + package.ContentIdentity);
            }
            foreach (DeviceInventoryEvidence device in inventory.Devices)
            {
                string[] hardwareIds =
                    (string[])device.HardwareIds.Clone();
                Array.Sort(
                    hardwareIds,
                    StringComparer.OrdinalIgnoreCase);
                lines.Add(String.Join("|", new[]
                {
                    "D",
                    device.InstanceId.ToUpperInvariant(),
                    device.Present.ToString(
                        CultureInfo.InvariantCulture),
                    String.Join(",", hardwareIds),
                    device.Service,
                    device.BindingPublishedInf.ToUpperInvariant(),
                    device.BindingContentIdentity,
                    device.ContainerId,
                    device.Parent,
                    device.DevNodeStatus.ToString(
                        CultureInfo.InvariantCulture),
                    device.ProblemCode.ToString(
                        CultureInfo.InvariantCulture)
                }));
            }
            return Sha256Text(String.Join("\n", lines.ToArray()));
        }

        internal static string Sha256File(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string parent = Path.GetDirectoryName(fullPath);
            if (String.IsNullOrWhiteSpace(parent))
            {
                throw new InvalidOperationException(
                    "Hash path has no parent directory.");
            }
            using (var leases = new ReadOnlyLeaseSet())
            {
                ReadOnlyPathLease root = leases.Add(
                    ReadOnlyPathLease.OpenRootDirectory(parent));
                ReadOnlyPathLease file = leases.Add(
                    ReadOnlyPathLease.OpenFile(fullPath, root));
                return Sha256Lease(file);
            }
        }

        internal static string Sha256Lease(ReadOnlyPathLease lease)
        {
            using (SHA256 algorithm = SHA256.Create())
            using (FileStream stream = lease.OpenReadStream())
            {
                return Hex(algorithm.ComputeHash(stream));
            }
        }

        internal static string Sha256Text(string value)
        {
            using (SHA256 algorithm = SHA256.Create())
            {
                return Hex(algorithm.ComputeHash(
                    Encoding.UTF8.GetBytes(value)));
            }
        }

        private static string Hex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (byte value in bytes)
            {
                builder.Append(value.ToString(
                    "x2",
                    CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }
    }

    internal sealed class DriverSignatureEvidence
    {
        internal bool Valid;
        internal bool TimestampValid;
        internal string SignerSubject;
        internal string SignerThumbprint;
        internal string TimestampThumbprint;
        internal string TimestampUtc;
        internal string TimestampType;
        internal string TimestampOid;
        internal bool TimestampChainValid;
        internal string TimestampChainStatus;
    }

    internal interface IDriverSignatureInspector
    {
        DriverSignatureEvidence Inspect(string catalogPath);
    }

    internal sealed class WindowsDriverSignatureInspector :
        IDriverSignatureInspector
    {
        internal const string Rfc3161Oid =
            "1.3.6.1.4.1.311.3.3.1";
        internal const string CounterSignatureOid =
            "1.2.840.113549.1.9.6";
        internal const string TstInfoContentTypeOid =
            "1.2.840.113549.1.9.16.1.4";
        internal const string TimeStampingEkuOid =
            "1.3.6.1.5.5.7.3.8";
        private const string SigningTimeOid =
            "1.2.840.113549.1.9.5";

        public DriverSignatureEvidence Inspect(string catalogPath)
        {
            var cms = new SignedCms();
            cms.Decode(File.ReadAllBytes(catalogPath));
            cms.CheckSignature(true);
            if (cms.SignerInfos.Count != 1 ||
                cms.SignerInfos[0].Certificate == null)
            {
                throw new InvalidOperationException(
                    "Catalog signer evidence is ambiguous.");
            }
            SignerInfo signer = cms.SignerInfos[0];
            SignerInfo timestampSigner = null;
            DateTime timestampUtc = DateTime.MinValue;
            string timestampType = String.Empty;
            string timestampOid = String.Empty;
            foreach (CryptographicAttributeObject attribute in
                signer.UnsignedAttributes)
            {
                if (attribute.Oid.Value == Rfc3161Oid &&
                    attribute.Values.Count == 1)
                {
                    X509Certificate2 timestampCertificate;
                    timestampUtc = ValidateRfc3161Token(
                        UnwrapOctetString(
                            attribute.Values[0].RawData),
                        signer.GetSignature(),
                        out timestampCertificate);
                    timestampType = "RFC3161";
                    timestampOid = Rfc3161Oid;
                    using (timestampCertificate)
                    {
                        string chainStatus;
                        bool chainValid = BuildTimestampChain(
                            timestampCertificate,
                            timestampUtc,
                            out chainStatus);
                        return BuildEvidence(
                            signer.Certificate,
                            timestampCertificate,
                            timestampUtc,
                            timestampType,
                            timestampOid,
                            chainValid,
                            chainStatus);
                    }
                }
            }
            if (timestampSigner == null &&
                signer.CounterSignerInfos.Count == 1)
            {
                timestampSigner = signer.CounterSignerInfos[0];
                timestampSigner.CheckSignature(true);
                timestampUtc = ReadSigningTime(timestampSigner);
                timestampType = "AuthenticodeCountersignature";
                timestampOid = CounterSignatureOid;
            }
            if (timestampSigner == null ||
                timestampSigner.Certificate == null ||
                timestampUtc == DateTime.MinValue)
            {
                throw new InvalidOperationException(
                    "Catalog has no verifiable signing timestamp.");
            }
            RequireTimestampCertificatePolicy(
                timestampSigner.Certificate);
            string legacyChainStatus;
            bool legacyChainValid = BuildTimestampChain(
                timestampSigner.Certificate,
                timestampUtc,
                out legacyChainStatus);
            return BuildEvidence(
                signer.Certificate,
                timestampSigner.Certificate,
                timestampUtc,
                timestampType,
                timestampOid,
                legacyChainValid,
                legacyChainStatus);
        }

        private static DriverSignatureEvidence BuildEvidence(
            X509Certificate2 signerCertificate,
            X509Certificate2 timestampCertificate,
            DateTime timestampUtc,
            string timestampType,
            string timestampOid,
            bool chainValid,
            string chainStatus)
        {
            return new DriverSignatureEvidence
            {
                Valid = true,
                TimestampValid = true,
                SignerSubject = signerCertificate.Subject,
                SignerThumbprint =
                    NormalizeThumbprint(
                        signerCertificate.Thumbprint),
                TimestampThumbprint = NormalizeThumbprint(
                    timestampCertificate.Thumbprint),
                TimestampUtc = new DateTimeOffset(
                    DateTime.SpecifyKind(
                        timestampUtc,
                        DateTimeKind.Utc)).ToString(
                            "o",
                            CultureInfo.InvariantCulture),
                TimestampType = timestampType,
                TimestampOid = timestampOid,
                TimestampChainValid = chainValid,
                TimestampChainStatus = chainStatus
            };
        }

        internal static DateTime ValidateRfc3161Token(
            byte[] encodedToken,
            byte[] parentSignature,
            out X509Certificate2 timestampCertificate)
        {
            var timestampCms = new SignedCms();
            timestampCms.Decode(encodedToken);
            if (timestampCms.ContentInfo.ContentType.Value !=
                TstInfoContentTypeOid)
            {
                throw new InvalidOperationException(
                    "RFC3161 token content type is not TSTInfo.");
            }
            timestampCms.CheckSignature(true);
            if (timestampCms.SignerInfos.Count != 1 ||
                timestampCms.SignerInfos[0].Certificate == null)
            {
                throw new InvalidOperationException(
                    "RFC3161 timestamp signer is ambiguous.");
            }
            DateTime timestampUtc =
                ValidateRfc3161MessageImprint(
                    timestampCms.ContentInfo.Content,
                    parentSignature);
            RequireTimestampCertificatePolicy(
                timestampCms.SignerInfos[0].Certificate);
            timestampCertificate = new X509Certificate2(
                timestampCms.SignerInfos[0].Certificate);
            return timestampUtc;
        }

        private static DateTime ReadSigningTime(SignerInfo signer)
        {
            foreach (CryptographicAttributeObject attribute in
                signer.SignedAttributes)
            {
                if (attribute.Oid.Value == SigningTimeOid &&
                    attribute.Values.Count == 1)
                {
                    return new Pkcs9SigningTime(
                        attribute.Values[0].RawData).SigningTime
                        .ToUniversalTime();
                }
            }
            throw new InvalidOperationException(
                "Authenticode countersignature has no signing time.");
        }

        private static bool BuildTimestampChain(
            X509Certificate2 certificate,
            DateTime verificationTime,
            out string status)
        {
            using (var chain = new X509Chain())
            {
                chain.ChainPolicy.RevocationMode =
                    X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationFlags =
                    X509VerificationFlags.NoFlag;
                chain.ChainPolicy.VerificationTime =
                    verificationTime.ToLocalTime();
                chain.ChainPolicy.ApplicationPolicy.Add(
                    new Oid(TimeStampingEkuOid));
                bool valid = chain.Build(certificate);
                var statuses = new List<string>();
                foreach (X509ChainStatus item in chain.ChainStatus)
                {
                    statuses.Add(item.Status.ToString());
                }
                status = statuses.Count == 0 ?
                    "NoError" :
                    String.Join(",", statuses.ToArray());
                return valid;
            }
        }

        internal static DateTime ValidateRfc3161MessageImprint(
            byte[] tstInfo,
            byte[] parentSignature)
        {
            if (tstInfo == null || parentSignature == null)
            {
                throw new ArgumentNullException(
                    "RFC3161 binding evidence is incomplete.");
            }
            var outer = new DerReader(tstInfo);
            DerValue sequence = outer.Read(0x30);
            outer.RequireEnd();
            var fields = new DerReader(sequence.Content);
            DerValue version = fields.Read(0x02);
            if (version.Content.Length != 1 ||
                version.Content[0] != 1)
            {
                throw new InvalidOperationException(
                    "RFC3161 TSTInfo version must be 1.");
            }
            DecodeOid(fields.Read(0x06).Content);
            DerValue imprintValue = fields.Read(0x30);
            ValidatePositiveDerInteger(
                fields.Read(0x02).Content,
                "TSTInfo serial");
            DerValue genTime = fields.Read(0x18);

            var imprint = new DerReader(imprintValue.Content);
            DerValue algorithmValue = imprint.Read(0x30);
            byte[] expectedDigest = imprint.Read(0x04).Content;
            imprint.RequireEnd();
            var algorithm = new DerReader(algorithmValue.Content);
            string hashOid = DecodeOid(
                algorithm.Read(0x06).Content);
            if (!algorithm.AtEnd)
            {
                DerValue parameters = algorithm.ReadAny();
                if (parameters.Tag != 0x05 ||
                    parameters.Content.Length != 0)
                {
                    throw new InvalidOperationException(
                        "RFC3161 hash AlgorithmIdentifier parameters " +
                        "must be absent or DER NULL.");
                }
            }
            algorithm.RequireEnd();

            byte[] actualDigest = HashParentSignature(
                hashOid,
                parentSignature);
            if (!FixedTimeEquals(expectedDigest, actualDigest))
            {
                throw new InvalidOperationException(
                    "RFC3161 messageImprint does not bind the " +
                    "catalog signer signature.");
            }
            return ParseGeneralizedTime(genTime.Content);
        }

        internal static void RequireTimestampCertificatePolicy(
            X509Certificate2 certificate)
        {
            if (certificate == null)
            {
                throw new ArgumentNullException("certificate");
            }
            X509EnhancedKeyUsageExtension timestampEku = null;
            foreach (X509Extension extension in certificate.Extensions)
            {
                var eku = extension as
                    X509EnhancedKeyUsageExtension;
                if (eku != null)
                {
                    timestampEku = eku;
                    break;
                }
            }
            if (timestampEku == null ||
                !timestampEku.Critical ||
                timestampEku.EnhancedKeyUsages.Count != 1 ||
                timestampEku.EnhancedKeyUsages[0].Value !=
                    TimeStampingEkuOid)
            {
                throw new InvalidOperationException(
                    "Timestamp signer certificate must have a " +
                    "critical, exclusive id-kp-timeStamping EKU.");
            }
        }

        private static DateTime ParseGeneralizedTime(byte[] value)
        {
            string text = Encoding.ASCII.GetString(value);
            string format;
            if (Regex.IsMatch(
                text,
                "^[0-9]{14}Z$",
                RegexOptions.CultureInvariant))
            {
                format = "yyyyMMddHHmmss'Z'";
            }
            else
            {
                Match fractional = Regex.Match(
                    text,
                    "^[0-9]{14}\\.([0-9]{1,7})Z$",
                    RegexOptions.CultureInvariant);
                if (!fractional.Success ||
                    fractional.Groups[1].Value.EndsWith(
                        "0",
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "RFC3161 genTime is not canonical DER.");
                }
                format = "yyyyMMddHHmmss." +
                    new string(
                        'f',
                        fractional.Groups[1].Value.Length) +
                    "'Z'";
            }
            DateTime result;
            if (!DateTime.TryParseExact(
                text,
                format,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal |
                    DateTimeStyles.AdjustToUniversal,
                out result))
            {
                throw new InvalidOperationException(
                    "RFC3161 genTime is invalid.");
            }
            return result;
        }

        private static void ValidatePositiveDerInteger(
            byte[] value,
            string label)
        {
            if (value == null ||
                value.Length == 0 ||
                (value[0] & 0x80) != 0 ||
                (value.Length == 1 && value[0] == 0) ||
                (value.Length > 1 &&
                    value[0] == 0 &&
                    (value[1] & 0x80) == 0))
            {
                throw new InvalidOperationException(
                    label +
                    " must be a positive, nonzero, minimally encoded " +
                    "DER INTEGER.");
            }
        }

        private static byte[] HashParentSignature(
            string hashOid,
            byte[] signature)
        {
            HashAlgorithm hash;
            if (hashOid == "1.3.14.3.2.26")
            {
                hash = SHA1.Create();
            }
            else if (hashOid == "2.16.840.1.101.3.4.2.1")
            {
                hash = SHA256.Create();
            }
            else if (hashOid == "2.16.840.1.101.3.4.2.2")
            {
                hash = SHA384.Create();
            }
            else if (hashOid == "2.16.840.1.101.3.4.2.3")
            {
                hash = SHA512.Create();
            }
            else
            {
                throw new InvalidOperationException(
                    "RFC3161 messageImprint hash is unsupported: " +
                    hashOid);
            }
            using (hash)
            {
                return hash.ComputeHash(signature);
            }
        }

        private static bool FixedTimeEquals(
            byte[] left,
            byte[] right)
        {
            int difference = left.Length ^ right.Length;
            int count = Math.Max(left.Length, right.Length);
            for (int index = 0; index < count; ++index)
            {
                byte leftValue = index < left.Length ?
                    left[index] :
                    (byte)0;
                byte rightValue = index < right.Length ?
                    right[index] :
                    (byte)0;
                difference |= leftValue ^ rightValue;
            }
            return difference == 0;
        }

        private static string DecodeOid(byte[] value)
        {
            if (value == null || value.Length == 0)
            {
                throw new InvalidOperationException(
                    "DER object identifier is empty.");
            }
            var parts = new List<ulong>();
            int offset = 0;
            ulong first = ReadOidComponent(value, ref offset);
            ulong firstArc = first < 40 ? 0 :
                (first < 80 ? 1UL : 2UL);
            parts.Add(firstArc);
            parts.Add(first - firstArc * 40);
            while (offset < value.Length)
            {
                parts.Add(ReadOidComponent(value, ref offset));
            }
            return String.Join(
                ".",
                parts.ConvertAll(
                    delegate(ulong part)
                    {
                        return part.ToString(
                            CultureInfo.InvariantCulture);
                    }).ToArray());
        }

        private static ulong ReadOidComponent(
            byte[] value,
            ref int offset)
        {
            ulong component = 0;
            bool firstByte = true;
            while (offset < value.Length)
            {
                byte item = value[offset++];
                if (firstByte && item == 0x80)
                {
                    throw new InvalidOperationException(
                        "DER object identifier is not canonical.");
                }
                firstByte = false;
                if (component > (UInt64.MaxValue >> 7))
                {
                    throw new InvalidOperationException(
                        "DER object identifier is too large.");
                }
                component = (component << 7) |
                    ((ulong)item & 0x7FUL);
                if ((item & 0x80) == 0)
                {
                    return component;
                }
            }
            throw new InvalidOperationException(
                "DER object identifier is truncated.");
        }

        private sealed class DerValue
        {
            internal int Tag;
            internal byte[] Content;
        }

        private sealed class DerReader
        {
            private readonly byte[] value;
            private int offset;

            internal DerReader(byte[] value)
            {
                this.value = value;
            }

            internal bool AtEnd
            {
                get { return offset == value.Length; }
            }

            internal DerValue Read(int expectedTag)
            {
                DerValue result = ReadAny();
                if (result.Tag != expectedTag)
                {
                    throw new InvalidOperationException(
                        "TSTInfo DER field order or type is invalid.");
                }
                return result;
            }

            internal DerValue ReadAny()
            {
                if (offset >= value.Length)
                {
                    throw new InvalidOperationException(
                        "TSTInfo DER is truncated.");
                }
                int tag = value[offset++];
                int length = ReadDerLength(value, ref offset);
                int end = checked(offset + length);
                if (end > value.Length)
                {
                    throw new InvalidOperationException(
                        "TSTInfo DER length is invalid.");
                }
                var content = new byte[length];
                Buffer.BlockCopy(value, offset, content, 0, length);
                offset = end;
                return new DerValue
                {
                    Tag = tag,
                    Content = content
                };
            }

            internal void RequireEnd()
            {
                if (!AtEnd)
                {
                    throw new InvalidOperationException(
                        "TSTInfo DER has unexpected trailing fields.");
                }
            }
        }

        private static byte[] UnwrapOctetString(byte[] value)
        {
            if (value.Length == 0 || value[0] != 0x04)
            {
                return value;
            }
            int offset = 1;
            int length = ReadDerLength(value, ref offset);
            if (offset + length != value.Length)
            {
                throw new InvalidOperationException(
                    "RFC3161 token wrapper is invalid.");
            }
            var result = new byte[length];
            Buffer.BlockCopy(value, offset, result, 0, length);
            return result;
        }

        private static int ReadDerLength(byte[] value, ref int offset)
        {
            if (offset >= value.Length)
            {
                throw new InvalidOperationException(
                    "Timestamp DER is truncated.");
            }
            int first = value[offset++];
            if ((first & 0x80) == 0)
            {
                return first;
            }
            int count = first & 0x7F;
            if (count == 0 || count > 4 ||
                offset + count > value.Length)
            {
                throw new InvalidOperationException(
                    "Timestamp DER length is invalid.");
            }
            if (value[offset] == 0)
            {
                throw new InvalidOperationException(
                    "Timestamp DER length is not minimally encoded.");
            }
            int length = 0;
            for (int index = 0; index < count; ++index)
            {
                length = checked((length << 8) | value[offset++]);
            }
            if (length < 128)
            {
                throw new InvalidOperationException(
                    "Timestamp DER length used a non-minimal long form.");
            }
            return length;
        }

        private static string NormalizeThumbprint(string value)
        {
            string normalized = Regex.Replace(
                value ?? String.Empty,
                "[^0-9A-Fa-f]",
                String.Empty).ToUpperInvariant();
            if (!IsCertificateThumbprint(normalized))
            {
                throw new InvalidOperationException(
                    "Driver signature certificate identity is invalid.");
            }
            return normalized;
        }

        internal static bool IsCertificateThumbprint(string value)
        {
            if (String.IsNullOrEmpty(value) || value.Length != 40)
            {
                return false;
            }
            foreach (char character in value)
            {
                if (!Uri.IsHexDigit(character))
                {
                    return false;
                }
            }
            return true;
        }
    }

    internal static class WindowsInventoryNative
    {
        internal const uint DnPresent = 0x00000002;
        private const int ErrorInsufficientBuffer = 122;

        [DllImport(
            "setupapi.dll",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern bool SetupGetInfDriverStoreLocation(
            string fileName,
            IntPtr alternatePlatformInfo,
            string localeName,
            StringBuilder returnBuffer,
            uint returnBufferSize,
            out uint requiredSize);

        [DllImport(
            "cfgmgr32.dll",
            CharSet = CharSet.Unicode)]
        private static extern uint CM_Locate_DevNode(
            out uint deviceInstance,
            string deviceInstanceId,
            uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_DevNode_Status(
            out uint status,
            out uint problemNumber,
            uint deviceInstance,
            uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_Parent(
            out uint parentDeviceInstance,
            uint deviceInstance,
            uint flags);

        [DllImport("cfgmgr32.dll")]
        private static extern uint CM_Get_Device_ID_Size(
            out uint length,
            uint deviceInstance,
            uint flags);

        [DllImport(
            "cfgmgr32.dll",
            CharSet = CharSet.Unicode)]
        private static extern uint CM_Get_Device_ID(
            uint deviceInstance,
            StringBuilder buffer,
            uint bufferLength,
            uint flags);

        internal static string ResolveDriverStoreInf(string publishedInf)
        {
            uint required;
            bool first = SetupGetInfDriverStoreLocation(
                publishedInf,
                IntPtr.Zero,
                null,
                null,
                0,
                out required);
            int error = Marshal.GetLastWin32Error();
            if (!first && error != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(
                    error,
                    "SetupGetInfDriverStoreLocation size query failed.");
            }
            if (required < 2)
            {
                throw new InvalidOperationException(
                    "Driver Store INF location length is invalid.");
            }
            var buffer = new StringBuilder((int)required);
            if (!SetupGetInfDriverStoreLocation(
                publishedInf,
                IntPtr.Zero,
                null,
                buffer,
                required,
                out required))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "SetupGetInfDriverStoreLocation failed.");
            }
            return Path.GetFullPath(buffer.ToString());
        }

        internal static void ReadDevNodeEvidence(
            string instanceId,
            out uint status,
            out uint problem,
            out string parentInstanceId)
        {
            uint deviceInstance;
            uint locate = CM_Locate_DevNode(
                out deviceInstance,
                instanceId,
                1);
            if (locate != 0)
            {
                throw new InvalidOperationException(
                    "CM_Locate_DevNode failed for " + instanceId +
                    " with CONFIGRET " +
                    locate.ToString(CultureInfo.InvariantCulture) + ".");
            }
            uint result = CM_Get_DevNode_Status(
                out status,
                out problem,
                deviceInstance,
                0);
            if (result != 0)
            {
                throw new InvalidOperationException(
                    "CM_Get_DevNode_Status failed for " + instanceId +
                    " with CONFIGRET " +
                    result.ToString(CultureInfo.InvariantCulture) + ".");
            }
            uint parent;
            result = CM_Get_Parent(
                out parent,
                deviceInstance,
                0);
            if (result != 0)
            {
                throw new InvalidOperationException(
                    "CM_Get_Parent failed for " + instanceId +
                    " with CONFIGRET " +
                    result.ToString(CultureInfo.InvariantCulture) + ".");
            }
            uint length;
            result = CM_Get_Device_ID_Size(
                out length,
                parent,
                0);
            if (result != 0 || length == 0)
            {
                throw new InvalidOperationException(
                    "CM_Get_Device_ID_Size failed for parent of " +
                    instanceId + " with CONFIGRET " +
                    result.ToString(CultureInfo.InvariantCulture) + ".");
            }
            var buffer = new StringBuilder((int)length + 1);
            result = CM_Get_Device_ID(
                parent,
                buffer,
                (uint)buffer.Capacity,
                0);
            if (result != 0 ||
                String.IsNullOrWhiteSpace(buffer.ToString()))
            {
                throw new InvalidOperationException(
                    "CM_Get_Device_ID failed for parent of " +
                    instanceId + " with CONFIGRET " +
                    result.ToString(CultureInfo.InvariantCulture) + ".");
            }
            parentInstanceId = buffer.ToString();
        }
    }
}
