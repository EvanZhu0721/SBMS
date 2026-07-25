using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace SBMSSetup
{
    internal static class WindowsInstallerInventoryTests
    {
        private static int passed;
        private static int failed;

        private sealed class FakeInventoryProvider :
            IWindowsInventoryProvider
        {
            internal WindowsDriverInventory First;
            internal WindowsDriverInventory Second;
            internal int Calls;

            public WindowsDriverInventory Inspect(
                InstallerOwnershipPolicy candidatePolicy)
            {
                ++Calls;
                return Calls == 1 ? First : Second;
            }
        }

        private sealed class FakeStateProbe :
            IInstallerAuditStateProbe
        {
            internal InstallerAuditState First;
            internal InstallerAuditState Second;
            internal int Calls;

            public InstallerAuditState Inspect()
            {
                ++Calls;
                return Calls == 1 ? First : Second;
            }
        }

        private sealed class FakeDeviceSystemEvidenceReader :
            IDeviceSystemEvidenceReader
        {
            internal DeviceSystemEvidence Evidence;
            internal int Calls;

            public DeviceSystemEvidence Read(string instanceId)
            {
                ++Calls;
                return Evidence;
            }
        }

        private sealed class FakeDriverSignatureInspector :
            IDriverSignatureInspector
        {
            internal DriverSignatureEvidence Evidence;

            public DriverSignatureEvidence Inspect(string catalogPath)
            {
                return Evidence;
            }
        }

        private static void Main(string[] arguments)
        {
            if (arguments.Length == 1 &&
                arguments[0] == "--bounded-child")
            {
                System.Threading.Thread.Sleep(5000);
                return;
            }
            Run("PnPUtil XML fixture parses machine fields", delegate
            {
                TestFixture(arguments[0]);
            });
            Run("PnPUtil XML ignores localized status text", delegate
            {
                TestLocalizedStatus(arguments[1]);
            });
            Run("independent device XML exposes active binding and relations",
                delegate { TestDeviceFixture(arguments[1]); });
            Run("active binding requires independent readback agreement",
                TestActiveBindingAgreement);
            Run("device production seam cross-checks CM and registry evidence",
                TestDeviceEvidenceCrossCheck);
            Run("PnPUtil XML rejects traversal and duplicate locators",
                TestParserRejectsUnsafeIdentity);
            Run("production package requires complete files evidence",
                TestExpectedPackageFiles);
            Run("read-only native process timeout is bounded",
                TestBoundedProcessTimeout);
            Run("ownership requires every independent identity",
                TestOwnershipFailClosed);
            Run("monitor-like identity alone never grants ownership",
                TestMonitorIdentityIsInsufficient);
            Run("AuditOnly captures before and after with no write API",
                TestAuditUnchanged);
            Run("AuditOnly rejects inventory or state drift",
                TestAuditDrift);
            Run("tree evidence is read-only and content-sensitive",
                TestTreeFingerprint);
            Run("tree evidence rejects reparse points before traversal",
                TestReparsePointRejection);
            Run("handle leases deny replacement and root escape",
                TestHandleLeaseProtection);
            Run("display classification excludes indirect and virtual paths",
                TestDisplayClassification);
            Run("signature inspector seam persists timestamp provenance",
                delegate
                {
                    TestSignatureEvidence(arguments[2], true);
                    TestSignatureEvidence(arguments[3], false);
                });
            Run("RFC3161 imprint binds the parent catalog signature",
                TestRfc3161Binding);
            Run("timestamp signer requires exclusive critical EKU",
                delegate { TestTimestampEku(arguments[4]); });
            Run("cryptographically valid unrelated RFC3161 token is rejected",
                delegate { TestSignedRfc3161Binding(arguments[5]); });
            Run("TSTInfo DER rejects every noncanonical encoding class",
                TestTstInfoCanonicalDer);
            if (arguments.Length > 6 &&
                String.Equals(
                    arguments[6],
                    "--live",
                    StringComparison.Ordinal))
            {
                Run("live production device reader cross-checks connected PnP record",
                    TestLiveProductionDeviceReader);
                Run("live production CAT inspector extracts timestamp provenance",
                    TestLiveWindowsCatalogInspector);
                Run("live AuditOnly leaves Windows state unchanged",
                    TestLiveAudit);
            }

            if (failed != 0)
            {
                Console.Error.WriteLine(
                    failed + " Windows installer inventory test(s) failed.");
                Environment.Exit(1);
            }
            Console.WriteLine(
                passed + " Windows installer inventory test(s) passed.");
        }

        private static void TestFixture(string fixturePath)
        {
            string xml = File.ReadAllText(fixturePath);
            PnpUtilDriverRecord[] records =
                PnpUtilXmlParser.Parse(xml);
            Assert(records.Length == 2, "Expected two packages.");
            PnpUtilDriverRecord sbms = records[0];
            Assert(
                sbms.PublishedInf == "oem56.inf",
                "Published locator mismatch.");
            Assert(
                sbms.OriginalInf == "SBMSIndirectDisplay.inf",
                "Original INF mismatch.");
            Assert(
                sbms.ClassGuid ==
                    "{4D36E968-E325-11CE-BFC1-08002BE10318}",
                "Class GUID was not normalized.");
            Assert(
                sbms.Files.Length == 1 &&
                sbms.DeviceInstanceIds.Length == 0,
                "Driver package file evidence mismatch.");
            Assert(
                sbms.CatalogAttributes.Length == 2,
                "Catalog provenance attributes missing.");
        }

        private static void TestLocalizedStatus(string fixturePath)
        {
            string xml = File.ReadAllText(fixturePath);
            PnpUtilDeviceRecord[] records =
                PnpUtilXmlParser.ParseDevices(xml);
            Assert(
                records[0].InstanceId ==
                    @"SWD\SBMS\VirtualDisplay-0001",
                "Localized status changed machine identity.");
            Assert(
                xml.IndexOf("已启动", StringComparison.Ordinal) >= 0,
                "Fixture no longer exercises localized status.");
        }

        private static void TestDeviceFixture(string fixturePath)
        {
            PnpUtilDeviceRecord[] records =
                PnpUtilXmlParser.ParseDevices(
                    File.ReadAllText(fixturePath));
            Assert(records.Length == 2, "Expected two devices.");
            Assert(
                records[0].ActivePublishedInf == "oem56.inf",
                "Active binding locator missing.");
            Assert(
                records[0].HardwareIds.Length == 1 &&
                records[0].HardwareIds[0] ==
                    @"SBMS\IndirectDisplay",
                "Exact production hardware ID missing.");
            Assert(
                records[0].Parent == @"HTREE\ROOT\0" &&
                records[0].Service == "WUDFRd",
                "Device relation/service evidence missing.");
        }

        private static void TestActiveBindingAgreement()
        {
            WindowsInventoryProvider.RequireActiveBindingAgreement(
                @"SWD\SBMS\VirtualDisplay-0001",
                "oem56.inf",
                "OEM56.INF");
            AssertThrows(delegate
            {
                WindowsInventoryProvider.RequireActiveBindingAgreement(
                    @"SWD\SBMS\VirtualDisplay-0001",
                    "oem56.inf",
                    "oem12.inf");
            }, "disagrees");
            AssertThrows(delegate
            {
                WindowsInventoryProvider.RequireActiveBindingAgreement(
                    @"SWD\SBMS\VirtualDisplay-0001",
                    "oem56.inf",
                    String.Empty);
            }, "disagrees");
        }

        private static void TestDeviceEvidenceCrossCheck()
        {
            DriverPackageEvidence package = Package();
            var packages =
                new System.Collections.Generic.Dictionary<
                    string,
                    DriverPackageEvidence>(
                        StringComparer.OrdinalIgnoreCase);
            packages.Add(package.PublishedInf, package);
            var record = new PnpUtilDeviceRecord
            {
                InstanceId = @"SWD\SBMS\VirtualDisplay-0001",
                ActivePublishedInf = package.PublishedInf,
                HardwareIds = new[] { @"SBMS\IndirectDisplay" },
                Parent = @"HTREE\ROOT\0",
                Service = "WUDFRd"
            };
            var system = new DeviceSystemEvidence
            {
                Present = true,
                ActivePublishedInf = package.PublishedInf,
                HardwareIds = new[] { @"SBMS\IndirectDisplay" },
                Parent = @"HTREE\ROOT\0",
                Service = "WUDFRd",
                ContainerId =
                    "{00000000-0000-0000-0000-000000000001}",
                DevNodeStatus = 2,
                ProblemCode = 0
            };
            var reader = new FakeDeviceSystemEvidenceReader
            {
                Evidence = system
            };
            DeviceInventoryEvidence evidence =
                WindowsInventoryProvider.BuildDeviceEvidence(
                    record,
                    packages,
                    reader.Read(record.InstanceId));
            Assert(
                reader.Calls == 1 &&
                evidence.Parent == @"HTREE\ROOT\0" &&
                evidence.BindingContentIdentity ==
                    package.ContentIdentity,
                "Injected production evidence seam was bypassed.");

            system.Parent = @"PCI\VEN_1234\1";
            AssertThrows(delegate
            {
                WindowsInventoryProvider.BuildDeviceEvidence(
                    record,
                    packages,
                    system);
            }, "parent");
            system.Parent = record.Parent;
            system.Service = "Other";
            AssertThrows(delegate
            {
                WindowsInventoryProvider.BuildDeviceEvidence(
                    record,
                    packages,
                    system);
            }, "service");
            system.Service = record.Service;
            system.HardwareIds = new[] { @"SBMS\Wrong" };
            AssertThrows(delegate
            {
                WindowsInventoryProvider.BuildDeviceEvidence(
                    record,
                    packages,
                    system);
            }, "hardware IDs");
        }

        private static void TestParserRejectsUnsafeIdentity()
        {
            string template =
                "<PnpUtil><Driver DriverName=\"__PUBLISHED__\">" +
                "<OriginalName>__ORIGINAL__</OriginalName>" +
                "<ProviderName>P</ProviderName>" +
                "<ClassName>Display</ClassName>" +
                "<ClassGuid>{4d36e968-e325-11ce-bfc1-08002be10318}</ClassGuid>" +
                "<DriverVersion>1</DriverVersion>" +
                "<SignerName>S</SignerName>" +
                "<CatalogFile>x.cat</CatalogFile>" +
                "</Driver></PnpUtil>";
            AssertThrows(delegate
            {
                PnpUtilXmlParser.Parse(
                    template
                        .Replace("__PUBLISHED__", "oem1.inf")
                        .Replace("__ORIGINAL__", @"..\bad.inf"));
            }, "leaf");
            string one = template
                .Replace("__PUBLISHED__", "oem1.inf")
                .Replace("__ORIGINAL__", "safe.inf");
            string duplicate = one.Replace(
                "</PnpUtil>",
                one.Substring("<PnpUtil>".Length)
                    .Replace("</PnpUtil>", String.Empty) +
                "</PnpUtil>");
            AssertThrows(delegate
            {
                PnpUtilXmlParser.Parse(duplicate);
            }, "duplicate");
        }

        private static void TestExpectedPackageFiles()
        {
            InstallerOwnershipPolicy policy = Policy(Package());
            var complete = new System.Collections.Generic.SortedSet<string>(
                new[]
                {
                    "SBMSIndirectDisplay.inf",
                    "SBMSIndirectDisplay.dll",
                    "sbmsindirectdisplay.cat"
                },
                StringComparer.OrdinalIgnoreCase);
            WindowsInventoryProvider.RequireExpectedPackageFiles(
                complete,
                policy);
            complete.Remove("SBMSIndirectDisplay.dll");
            AssertThrows(delegate
            {
                WindowsInventoryProvider.RequireExpectedPackageFiles(
                    complete,
                    policy);
            }, "/files");
            complete.Add("SBMSIndirectDisplay.dll");
            complete.Add("unexpected.sys");
            AssertThrows(delegate
            {
                WindowsInventoryProvider.RequireExpectedPackageFiles(
                    complete,
                    policy);
            }, "/files");
        }

        private static void TestBoundedProcessTimeout()
        {
            var start = new System.Diagnostics.ProcessStartInfo();
            start.FileName =
                System.Reflection.Assembly.GetExecutingAssembly().Location;
            start.Arguments = "--bounded-child";
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            DateTime started = DateTime.UtcNow;
            AssertThrows(delegate
            {
                BoundedReadOnlyProcess.Run(
                    start,
                    100,
                    "fixture child");
            }, "timeout");
            Assert(
                (DateTime.UtcNow - started).TotalSeconds < 3,
                "Timeout did not terminate the child promptly.");
        }

        private static void TestOwnershipFailClosed()
        {
            DriverPackageEvidence package = Package();
            DeviceInventoryEvidence device = Device(package);
            InstallerOwnershipPolicy policy = Policy(package);
            Assert(
                InstallerOwnership.IsOwnedPackage(package, policy),
                "Complete package identity was not accepted.");
            Assert(
                InstallerOwnership.HasHealthyOwnedBinding(
                    device,
                    package,
                    policy),
                "Complete device identity was not accepted.");

            package.Provider = "Impostor";
            Assert(
                !InstallerOwnership.IsOwnedPackage(package, policy),
                "Provider mismatch was accepted.");
            package.Provider = "SBMS";
            package.TimestampVerified = false;
            Assert(
                !InstallerOwnership.IsOwnedPackage(package, policy),
                "Missing catalog timestamp evidence was accepted.");
            package.TimestampVerified = true;
            package.CatalogMembershipVerified = false;
            Assert(
                !InstallerOwnership.IsOwnedPackage(package, policy),
                "Missing catalog membership evidence was accepted.");
            package.CatalogMembershipVerified = true;
            device.Service = "OtherService";
            Assert(
                !InstallerOwnership.HasHealthyOwnedBinding(
                    device,
                    package,
                    policy),
                "Service mismatch was accepted.");
            device.Service = "WUDFRd";
            device.BindingContentIdentity = new string('f', 64);
            Assert(
                !InstallerOwnership.HasHealthyOwnedBinding(
                    device,
                    package,
                    policy),
                "Binding content mismatch was accepted.");
            device.BindingContentIdentity = package.ContentIdentity;
            device.ProblemCode = 43;
            Assert(
                !InstallerOwnership.HasHealthyOwnedBinding(
                    device,
                    package,
                    policy),
                "Problem device was accepted as removable owned state.");
            Assert(
                InstallerOwnership.IsOwnedResidualDevice(
                    device,
                    policy),
                "Problem residual device was not retained for orphan audit.");
            device.ProblemCode = 0;
            device.BindingPublishedInf = "oem999.inf";
            device.BindingContentIdentity = String.Empty;
            Assert(
                InstallerOwnership.IsOwnedResidualDevice(
                    device,
                    policy),
                "Wrong-bound residual device became invisible.");
            Assert(
                !InstallerOwnership.HasHealthyOwnedBinding(
                    device,
                    package,
                    policy),
                "Wrong-bound residual was treated as healthy.");
            device.ContainerId =
                "{00000000-0000-0000-0000-000000000099}";
            Assert(
                !InstallerOwnership.IsOwnedResidualDevice(
                    device,
                    policy),
                "Unknown container granted residual ownership.");
            device.ContainerId =
                "{00000000-0000-0000-0000-000000000001}";
            device.Parent = @"PCI\VEN_1234\1";
            Assert(
                !InstallerOwnership.IsOwnedResidualDevice(
                    device,
                    policy),
                "Unknown parent granted residual ownership.");
        }

        private static void TestMonitorIdentityIsInsufficient()
        {
            DriverPackageEvidence package = Package();
            DeviceInventoryEvidence device = Device(package);
            InstallerOwnershipPolicy policy = Policy(package);
            device.InstanceId = @"DISPLAY\SBMS0001\1";
            device.HardwareIds = new[]
            {
                "MONITOR\\SBMS0001"
            };
            Assert(
                !InstallerOwnership.IsOwnedResidualDevice(
                    device,
                    policy),
                "Monitor identity alone granted ownership.");
        }

        private static void TestAuditUnchanged()
        {
            WindowsDriverInventory inventory = Inventory();
            InstallerAuditState state = State("same");
            var inventoryProvider = new FakeInventoryProvider
            {
                First = inventory,
                Second = inventory
            };
            var stateProbe = new FakeStateProbe
            {
                First = state,
                Second = state
            };
            var audit = new InstallerAuditOnly(
                inventoryProvider,
                stateProbe);
            InstallerAuditReport report = audit.Run(
                Policy(inventory.Packages[0]));
            Assert(report.Unchanged, "Audit did not prove unchanged state.");
            Assert(
                inventoryProvider.Calls == 2 &&
                stateProbe.Calls == 2,
                "Audit did not capture both boundaries exactly once.");
            Assert(
                report.OwnedPackages.Length == 1 &&
                report.OwnedResidualDevices.Length == 1 &&
                report.HealthyActiveOwnedDevices.Length == 1,
                "Audit ownership result mismatch.");
            Assert(
                !report.BeforeState.JournalExists &&
                !report.AfterState.JournalExists &&
                !report.BeforeState.EscrowExists &&
                !report.AfterState.EscrowExists,
                "Audit created journal or escrow evidence.");
        }

        private static void TestAuditDrift()
        {
            WindowsDriverInventory before = Inventory();
            WindowsDriverInventory after = Inventory();
            after.EvidenceDigest = new string('b', 64);
            var inventoryProvider = new FakeInventoryProvider
            {
                First = before,
                Second = after
            };
            var stateProbe = new FakeStateProbe
            {
                First = State("same"),
                Second = State("same")
            };
            AssertThrows(delegate
            {
                new InstallerAuditOnly(
                    inventoryProvider,
                    stateProbe).Run(Policy(before.Packages[0]));
            }, "changing");

            inventoryProvider = new FakeInventoryProvider
            {
                First = before,
                Second = before
            };
            stateProbe = new FakeStateProbe
            {
                First = State("one"),
                Second = State("two")
            };
            AssertThrows(delegate
            {
                new InstallerAuditOnly(
                    inventoryProvider,
                    stateProbe).Run(Policy(before.Packages[0]));
            }, "changing");
        }

        private static void TestTreeFingerprint()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "SBMS-audit-tree-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "value.bin");
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
            try
            {
                string before =
                    WindowsInstallerAuditStateProbe
                        .ReadOnlyTreeFingerprint(root);
                string repeat =
                    WindowsInstallerAuditStateProbe
                        .ReadOnlyTreeFingerprint(root);
                Assert(
                    before == repeat,
                    "Read-only tree fingerprint was unstable.");
                File.WriteAllBytes(file, new byte[] { 1, 2, 4 });
                string after =
                    WindowsInstallerAuditStateProbe
                        .ReadOnlyTreeFingerprint(root);
                Assert(
                    before != after,
                    "Tree fingerprint missed content change.");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        private static void TestReparsePointRejection()
        {
            AssertThrows(delegate
            {
                WindowsInstallerAuditStateProbe
                    .RejectReparseAttributes(
                        "fixture-junction",
                        FileAttributes.Directory |
                        FileAttributes.ReparsePoint);
            }, "reparse");
            WindowsInstallerAuditStateProbe.RejectReparseAttributes(
                "fixture-directory",
                FileAttributes.Directory);
        }

        private static void TestHandleLeaseProtection()
        {
            string parent = Path.Combine(
                Path.GetTempPath(),
                "SBMS-audit-lease-" + Guid.NewGuid().ToString("N"));
            string root = Path.Combine(parent, "root");
            string outside = Path.Combine(parent, "outside.bin");
            Directory.CreateDirectory(root);
            string inside = Path.Combine(root, "inside.bin");
            File.WriteAllBytes(inside, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(outside, new byte[] { 4, 5, 6 });
            try
            {
                using (var leases = new ReadOnlyLeaseSet())
                {
                    ReadOnlyPathLease rootLease = leases.Add(
                        ReadOnlyPathLease.OpenRootDirectory(root));
                    leases.Add(ReadOnlyPathLease.OpenFile(
                        inside,
                        rootLease));
                    AssertAnyThrows(delegate
                    {
                        using (FileStream stream = File.Open(
                            inside,
                            FileMode.Open,
                            FileAccess.Write,
                            FileShare.ReadWrite))
                        {
                        }
                    });
                    AssertAnyThrows(delegate
                    {
                        File.Delete(inside);
                    });
                    AssertThrows(delegate
                    {
                        ReadOnlyPathLease.OpenFile(
                            outside,
                            rootLease);
                    }, "escaped");
                }
            }
            finally
            {
                Directory.Delete(parent, true);
            }
        }

        private static void TestDisplayClassification()
        {
            Assert(
                WindowsDisplayTopologyProbe.IsPhysical(5),
                "HDMI physical output was rejected.");
            Assert(
                WindowsDisplayTopologyProbe.IsPhysical(18),
                "USB tunneled DisplayPort output was rejected.");
            Assert(
                !WindowsDisplayTopologyProbe.IsPhysical(15) &&
                !WindowsDisplayTopologyProbe.IsPhysical(16),
                "Indirect/virtual output was classified physical.");
            Assert(
                WindowsDisplayTopologyProbe.IsUsablePhysicalPath(
                    18,
                    true,
                    1,
                    1),
                "Available in-use physical path was rejected.");
            Assert(
                !WindowsDisplayTopologyProbe.IsUsablePhysicalPath(
                    18,
                    false,
                    1,
                    1) &&
                !WindowsDisplayTopologyProbe.IsUsablePhysicalPath(
                    18,
                    true,
                    0,
                    1) &&
                !WindowsDisplayTopologyProbe.IsUsablePhysicalPath(
                    18,
                    true,
                    1,
                    0),
                "Unavailable or not-in-use display path was accepted.");
        }

        private static void TestRfc3161Binding()
        {
            byte[] parentSignature = new byte[] { 1, 2, 3, 4 };
            byte[] unrelatedSignature = new byte[] { 9, 8, 7, 6 };
            byte[] expected;
            using (SHA256 sha = SHA256.Create())
            {
                expected = sha.ComputeHash(parentSignature);
            }
            DateTime time =
                WindowsDriverSignatureInspector
                    .ValidateRfc3161MessageImprint(
                        BuildTstInfo(expected),
                        parentSignature);
            Assert(
                time == new DateTime(
                    2026,
                    7,
                    25,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc),
                "TSTInfo genTime was not parsed from its exact field.");

            byte[] unrelatedDigest;
            using (SHA256 sha = SHA256.Create())
            {
                unrelatedDigest =
                    sha.ComputeHash(unrelatedSignature);
            }
            AssertThrows(delegate
            {
                WindowsDriverSignatureInspector
                    .ValidateRfc3161MessageImprint(
                        BuildTstInfo(unrelatedDigest),
                        parentSignature);
            }, "does not bind");
            AssertThrows(delegate
            {
                WindowsDriverSignatureInspector
                    .ValidateRfc3161MessageImprint(
                        BuildTstInfo(new byte[32]),
                        parentSignature);
            }, "does not bind");
        }

        private static void TestTimestampEku(
            string certificateFixture)
        {
            byte[] raw = Convert.FromBase64String(
                File.ReadAllText(certificateFixture).Trim());
            using (var certificate = new X509Certificate2(raw))
            {
                AssertThrows(delegate
                {
                    WindowsDriverSignatureInspector
                        .RequireTimestampCertificatePolicy(
                            certificate);
                }, "id-kp-timeStamping");
            }
        }

        private static void TestSignedRfc3161Binding(
            string timestampPfxFixture)
        {
            byte[] parentSignature = new byte[] { 1, 2, 3, 4 };
            byte[] unrelatedSignature = new byte[] { 9, 8, 7, 6 };
            byte[] unrelatedDigest;
            using (SHA256 sha = SHA256.Create())
            {
                unrelatedDigest =
                    sha.ComputeHash(unrelatedSignature);
            }
            using (var certificate = new X509Certificate2(
                Convert.FromBase64String(
                    File.ReadAllText(
                        timestampPfxFixture).Trim()),
                "fixture",
                X509KeyStorageFlags.Exportable))
            {
                var content = new ContentInfo(
                    new Oid(
                        WindowsDriverSignatureInspector
                            .TstInfoContentTypeOid),
                    BuildTstInfo(unrelatedDigest));
                var cms = new SignedCms(content);
                cms.ComputeSignature(
                    new CmsSigner(certificate));
                X509Certificate2 timestampCertificate;
                AssertThrows(delegate
                {
                    WindowsDriverSignatureInspector
                        .ValidateRfc3161Token(
                            cms.Encode(),
                            parentSignature,
                            out timestampCertificate);
                }, "does not bind");

                var wrongCms = new SignedCms(
                    new ContentInfo(
                        new Oid(
                            WindowsDriverSignatureInspector
                                .TstInfoContentTypeOid),
                        BuildTstInfo(new byte[32])));
                wrongCms.ComputeSignature(
                    new CmsSigner(certificate));
                AssertThrows(delegate
                {
                    WindowsDriverSignatureInspector
                        .ValidateRfc3161Token(
                            wrongCms.Encode(),
                            parentSignature,
                            out timestampCertificate);
                }, "does not bind");
            }
        }

        private static void TestTstInfoCanonicalDer()
        {
            byte[] parentSignature = new byte[] { 1, 2, 3, 4 };
            byte[] digest;
            using (SHA256 sha = SHA256.Create())
            {
                digest = sha.ComputeHash(parentSignature);
            }
            byte[] canonical = BuildTstInfo(digest);
            DateTime absentParameters =
                WindowsDriverSignatureInspector
                    .ValidateRfc3161MessageImprint(
                        BuildTstInfoCustom(
                            digest,
                            new byte[0],
                            new byte[] { 0, 0x80 },
                            "20260725000000.123Z"),
                        parentSignature);
            Assert(
                absentParameters.Millisecond == 123,
                "Canonical absent parameters, positive serial or " +
                "fractional genTime was rejected.");
            byte[] content = new byte[canonical.Length - 2];
            Buffer.BlockCopy(
                canonical,
                2,
                content,
                0,
                content.Length);
            AssertThrows(delegate
            {
                WindowsDriverSignatureInspector
                    .ValidateRfc3161MessageImprint(
                        Join(
                            new byte[]
                            {
                                0x30,
                                0x81,
                                (byte)content.Length
                            },
                            content),
                        parentSignature);
            }, "non-minimal long form");
            AssertThrows(delegate
            {
                WindowsDriverSignatureInspector
                    .ValidateRfc3161MessageImprint(
                        Join(
                            new byte[]
                            {
                                0x30,
                                0x82,
                                0x00,
                                (byte)content.Length
                            },
                            content),
                        parentSignature);
            }, "minimally encoded");

            AssertThrows(delegate
            {
                WindowsDriverSignatureInspector
                    .ValidateRfc3161MessageImprint(
                        BuildTstInfoCustom(
                            digest,
                            Der(0x04, new byte[0]),
                            new byte[] { 1 },
                            "20260725000000Z"),
                        parentSignature);
            }, "AlgorithmIdentifier");
            AssertThrows(delegate
            {
                WindowsDriverSignatureInspector
                    .ValidateRfc3161MessageImprint(
                        BuildTstInfoCustom(
                            digest,
                            Join(
                                Der(0x05, new byte[0]),
                                Der(0x05, new byte[0])),
                            new byte[] { 1 },
                            "20260725000000Z"),
                        parentSignature);
            }, "trailing");

            foreach (byte[] serial in new[]
            {
                new byte[] { 0 },
                new byte[] { 0x80 },
                new byte[] { 0, 0x7F },
                new byte[] { 0xFF, 1 }
            })
            {
                byte[] invalidSerial = serial;
                AssertThrows(delegate
                {
                    WindowsDriverSignatureInspector
                        .ValidateRfc3161MessageImprint(
                            BuildTstInfoCustom(
                                digest,
                                Der(0x05, new byte[0]),
                                invalidSerial,
                                "20260725000000Z"),
                            parentSignature);
                }, "positive, nonzero, minimally encoded");
            }

            foreach (string genTime in new[]
            {
                "20260725000000.10Z",
                "20260725000000.Z",
                "20260725000000,1Z",
                "20260725000000.12345678Z",
                "20260725000000+0000"
            })
            {
                string invalidTime = genTime;
                AssertThrows(delegate
                {
                    WindowsDriverSignatureInspector
                        .ValidateRfc3161MessageImprint(
                            BuildTstInfoCustom(
                                digest,
                                Der(0x05, new byte[0]),
                                new byte[] { 1 },
                                invalidTime),
                            parentSignature);
                }, "canonical DER");
            }
        }

        private static byte[] BuildTstInfo(byte[] digest)
        {
            return BuildTstInfoCustom(
                digest,
                Der(0x05, new byte[0]),
                new byte[] { 1 },
                "20260725000000Z");
        }

        private static byte[] BuildTstInfoCustom(
            byte[] digest,
            byte[] algorithmParameters,
            byte[] serial,
            string genTime)
        {
            byte[] sha256Oid = new byte[]
            {
                0x60, 0x86, 0x48, 0x01, 0x65,
                0x03, 0x04, 0x02, 0x01
            };
            byte[] algorithm = Der(
                0x30,
                Join(
                    Der(0x06, sha256Oid),
                    algorithmParameters));
            byte[] imprint = Der(
                0x30,
                Join(
                    algorithm,
                    Der(0x04, digest)));
            return Der(
                0x30,
                Join(
                    Der(0x02, new byte[] { 1 }),
                    Der(0x06, new byte[] { 0x2A, 0x03 }),
                    imprint,
                    Der(0x02, serial),
                    Der(
                        0x18,
                        System.Text.Encoding.ASCII.GetBytes(
                            genTime))));
        }

        private static byte[] Der(byte tag, byte[] content)
        {
            if (content.Length >= 128)
            {
                throw new InvalidOperationException(
                    "Test DER helper only supports short lengths.");
            }
            return Join(
                new byte[] { tag, (byte)content.Length },
                content);
        }

        private static byte[] Join(params byte[][] values)
        {
            int length = 0;
            foreach (byte[] value in values)
            {
                length += value.Length;
            }
            var result = new byte[length];
            int offset = 0;
            foreach (byte[] value in values)
            {
                Buffer.BlockCopy(
                    value,
                    0,
                    result,
                    offset,
                    value.Length);
                offset += value.Length;
            }
            return result;
        }

        private static void TestSignatureEvidence(
            string fixturePath,
            bool expectedValid)
        {
            DriverSignatureEvidence evidence =
                ReadSignatureFixture(fixturePath);
            var inspector = new FakeDriverSignatureInspector
            {
                Evidence = evidence
            };
            DriverPackageEvidence package = Package();
            if (expectedValid)
            {
                WindowsInventoryProvider.ApplySignatureEvidence(
                    package,
                    inspector.Inspect(fixturePath));
                Assert(
                    package.TimestampUtc == evidence.TimestampUtc &&
                    package.TimestampType == evidence.TimestampType &&
                    package.TimestampOid == evidence.TimestampOid &&
                    package.TimestampChainValid &&
                    package.TimestampChainStatus ==
                        evidence.TimestampChainStatus,
                    "Timestamp provenance was not persisted.");
            }
            else
            {
                AssertThrows(delegate
                {
                    WindowsInventoryProvider.ApplySignatureEvidence(
                        package,
                        inspector.Inspect(fixturePath));
                }, "timestamp");
            }
        }

        private static DriverSignatureEvidence ReadSignatureFixture(
            string fixturePath)
        {
            string[] fields = File.ReadAllText(fixturePath).Trim().Split('|');
            if (fields.Length != 10)
            {
                throw new InvalidOperationException(
                    "Signature fixture field count is invalid.");
            }
            return new DriverSignatureEvidence
            {
                Valid = Boolean.Parse(fields[0]),
                TimestampValid = Boolean.Parse(fields[1]),
                SignerSubject = fields[2],
                SignerThumbprint = fields[3],
                TimestampThumbprint = fields[4],
                TimestampUtc = fields[5],
                TimestampType = fields[6],
                TimestampOid = fields[7],
                TimestampChainValid = Boolean.Parse(fields[8]),
                TimestampChainStatus = fields[9]
            };
        }

        private static void TestLiveProductionDeviceReader()
        {
            string xml = RunPnpUtil(
                "/enum-devices /connected /deviceids /relations " +
                "/drivers /services /format xml");
            PnpUtilDeviceRecord[] records =
                PnpUtilXmlParser.ParseDevices(xml);
            Exception lastFailure = null;
            foreach (PnpUtilDeviceRecord record in records)
            {
                if (String.IsNullOrWhiteSpace(record.Parent) ||
                    String.IsNullOrWhiteSpace(record.Service) ||
                    String.IsNullOrWhiteSpace(
                        record.ActivePublishedInf) ||
                    record.HardwareIds.Length == 0 ||
                    record.InstanceId.StartsWith(
                        @"ROOT\",
                        StringComparison.OrdinalIgnoreCase) ||
                    record.InstanceId.StartsWith(
                        @"HTREE\",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                try
                {
                    DeviceSystemEvidence system =
                        new WindowsDeviceSystemEvidenceReader().Read(
                            record.InstanceId);
                    WindowsInventoryProvider.BuildDeviceEvidence(
                        record,
                        new System.Collections.Generic.Dictionary<
                            string,
                            DriverPackageEvidence>(
                                StringComparer.OrdinalIgnoreCase),
                        system);
                    return;
                }
                catch (Exception failure)
                {
                    lastFailure = failure;
                }
            }
            throw new InvalidOperationException(
                "No non-root connected PnP record passed the production " +
                "device reader cross-check.",
                lastFailure);
        }

        private static void TestLiveWindowsCatalogInspector()
        {
            string xml = RunPnpUtil(
                "/enum-drivers /files /format xml");
            PnpUtilDriverRecord[] records =
                PnpUtilXmlParser.Parse(xml);
            string windows = Environment.GetFolderPath(
                Environment.SpecialFolder.Windows);
            Exception lastFailure = null;
            foreach (PnpUtilDriverRecord record in records)
            {
                if (String.IsNullOrWhiteSpace(record.PublishedInf) ||
                    String.IsNullOrWhiteSpace(record.CatalogFile))
                {
                    continue;
                }
                try
                {
                    string storeInf =
                        WindowsInventoryNative.ResolveDriverStoreInf(
                            Path.Combine(
                                windows,
                                "INF",
                                record.PublishedInf));
                    string root = Path.GetDirectoryName(storeInf);
                    using (var leases = new ReadOnlyLeaseSet())
                    {
                        ReadOnlyPathLease rootLease = leases.Add(
                            ReadOnlyPathLease.OpenRootDirectory(root));
                        ReadOnlyPathLease catalogLease = leases.Add(
                            ReadOnlyPathLease.OpenFile(
                                Path.Combine(root, record.CatalogFile),
                                rootLease));
                        DriverSignatureEvidence evidence =
                            new WindowsDriverSignatureInspector().Inspect(
                                catalogLease.RequestedPath);
                        if (evidence.Valid &&
                            evidence.TimestampValid &&
                            evidence.TimestampChainValid &&
                            evidence.TimestampType == "RFC3161" &&
                            evidence.TimestampOid ==
                                WindowsDriverSignatureInspector
                                    .Rfc3161Oid &&
                            !String.IsNullOrWhiteSpace(
                                evidence.TimestampUtc) &&
                            !String.IsNullOrWhiteSpace(
                                evidence.TimestampOid))
                        {
                            return;
                        }
                    }
                }
                catch (Exception failure)
                {
                    lastFailure = failure;
                }
            }
            throw new InvalidOperationException(
                "No installed Windows catalog passed the production " +
                "timestamp inspector. Last failure: " +
                (lastFailure == null ?
                    "none" :
                    lastFailure.ToString()),
                lastFailure);
        }

        private static string RunPnpUtil(string arguments)
        {
            var start = new ProcessStartInfo();
            start.FileName = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.System),
                "pnputil.exe");
            start.Arguments = arguments;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            BoundedProcessResult result = BoundedReadOnlyProcess.Run(
                start,
                30000,
                "live PnPUtil smoke");
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "PnPUtil live smoke failed: " +
                    result.StandardError);
            }
            return result.StandardOutput;
        }

        private static void TestLiveAudit()
        {
            string programData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            string localData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            string commonPrograms = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonPrograms);
            string userPrograms = Environment.GetFolderPath(
                Environment.SpecialFolder.Programs);
            string windows = Environment.GetFolderPath(
                Environment.SpecialFolder.Windows);
            string installerRoot = Path.Combine(
                programData,
                "SBMS",
                "Installer");
            var probe = new WindowsInstallerAuditStateProbe(
                Path.Combine(installerRoot, "journal.json"),
                Path.Combine(installerRoot, "transactions"),
                installerRoot,
                Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.ProgramFiles),
                    "SBMS"),
                Path.Combine(localData, "SBMS"),
                new[]
                {
                    Path.Combine(commonPrograms, "SBMS"),
                    Path.Combine(userPrograms, "SBMS"),
                    Path.Combine(
                        windows,
                        "System32",
                        "Tasks",
                        "SBMS")
                },
                WindowsDisplayTopologyProbe.Capture);
            var policy = new InstallerOwnershipPolicy
            {
                OriginalInf = "SBMSIndirectDisplay.inf",
                Provider = "SBMS",
                ClassGuid =
                    "{4D36E968-E325-11CE-BFC1-08002BE10318}",
                Services = new[] { "WUDFRd" },
                InstanceIdPrefixes = new[]
                {
                    @"SWD\SBMS\",
                    @"ROOT\SBMSINDIRECTDISPLAY\"
                },
                HardwareIds = new[]
                {
                    @"SBMS\IndirectDisplay",
                    @"Root\SBMSIndirectDisplay"
                },
                ContainerIds = new[]
                {
                    "{00000000-0000-0000-0000-000000000001}"
                },
                ParentInstanceIds = new[] { @"HTREE\ROOT\0" },
                ParentInstancePrefixes = new string[0],
                ApprovedPackageContentIdentities =
                    new[] { new string('0', 64) },
                ExpectedPackageFiles = new[]
                {
                    "SBMSIndirectDisplay.inf",
                    "SBMSIndirectDisplay.dll",
                    "sbmsindirectdisplay.cat"
                },
                ApprovedSigners = new[]
                {
                    "Microsoft Windows Hardware Compatibility Publisher"
                }
            };
            InstallerAuditReport report = new InstallerAuditOnly(
                new WindowsInventoryProvider(),
                probe).Run(policy);
            Assert(report.Unchanged, "Live audit was not unchanged.");
            Assert(
                report.BeforeInventory.Packages != null &&
                report.BeforeInventory.Devices != null,
                "Live candidate inventory was unavailable.");
            Assert(
                report.BeforeInventory.EvidenceDigest ==
                    report.AfterInventory.EvidenceDigest,
                "Live inventory digest changed.");
            Assert(
                report.BeforeState.EvidenceDigest ==
                    report.AfterState.EvidenceDigest,
                "Live installer/display state changed.");
        }

        private static WindowsDriverInventory Inventory()
        {
            DriverPackageEvidence package = Package();
            return new WindowsDriverInventory
            {
                Packages = new[] { package },
                Devices = new[] { Device(package) },
                EvidenceDigest = new string('a', 64)
            };
        }

        private static DriverPackageEvidence Package()
        {
            return new DriverPackageEvidence
            {
                PublishedInf = "oem56.inf",
                OriginalInf = "SBMSIndirectDisplay.inf",
                Provider = "SBMS",
                ClassName = "Display",
                ClassGuid =
                    "{4D36E968-E325-11CE-BFC1-08002BE10318}",
                DriverDateAndVersion = "07/25/2026 0.3.0.0",
                CatalogFile = "sbmsindirectdisplay.cat",
                Signer =
                    "Microsoft Windows Hardware Compatibility Publisher",
                WhcpVersion = "10.0",
                CatalogAttributes = new[] { "Declarative" },
                Files = new DriverFileEvidence[0],
                CatalogTrustVerified = true,
                CatalogMembershipVerified = true,
                TimestampVerified = true,
                SignerThumbprint = new string('2', 40),
                TimestampThumbprint = new string('3', 40),
                TimestampUtc = "2026-07-25T00:00:00.0000000+00:00",
                TimestampType = "RFC3161",
                TimestampOid =
                    WindowsDriverSignatureInspector.Rfc3161Oid,
                TimestampChainValid = true,
                TimestampChainStatus = "NoError",
                SignatureProvenance =
                    "WinVerifyTrust+CatalogMembership+Authenticode",
                ContentIdentity = new string('1', 64)
            };
        }

        private static DeviceInventoryEvidence Device(
            DriverPackageEvidence package)
        {
            return new DeviceInventoryEvidence
            {
                InstanceId = @"SWD\SBMS\VirtualDisplay-0001",
                Present = true,
                HardwareIds = new[] { @"SBMS\IndirectDisplay" },
                Service = "WUDFRd",
                BindingPublishedInf = package.PublishedInf,
                BindingContentIdentity = package.ContentIdentity,
                ContainerId = "{00000000-0000-0000-0000-000000000001}",
                Parent = "HTREE\\ROOT\\0",
                DevNodeStatus = 2,
                ProblemCode = 0
            };
        }

        private static InstallerOwnershipPolicy Policy(
            DriverPackageEvidence package)
        {
            return new InstallerOwnershipPolicy
            {
                OriginalInf = package.OriginalInf,
                Provider = package.Provider,
                ClassGuid = package.ClassGuid,
                Services = new[] { "WUDFRd" },
                InstanceIdPrefixes = new[]
                {
                    @"SWD\SBMS\",
                    @"ROOT\SBMSINDIRECTDISPLAY\"
                },
                HardwareIds = new[]
                {
                    @"SBMS\IndirectDisplay",
                    @"Root\SBMSIndirectDisplay"
                },
                ContainerIds = new[]
                {
                    "{00000000-0000-0000-0000-000000000001}"
                },
                ParentInstanceIds = new[] { @"HTREE\ROOT\0" },
                ParentInstancePrefixes = new string[0],
                ExpectedPackageFiles = new[]
                {
                    "SBMSIndirectDisplay.inf",
                    "SBMSIndirectDisplay.dll",
                    "sbmsindirectdisplay.cat"
                },
                ApprovedPackageContentIdentities =
                    new[] { package.ContentIdentity },
                ApprovedSigners = new[] { package.Signer }
            };
        }

        private static InstallerAuditState State(string marker)
        {
            return new InstallerAuditState
            {
                JournalExists = false,
                EscrowExists = false,
                InstallerStateFingerprint = marker,
                PayloadFingerprint = "payload",
                ConfigurationFingerprint = "config",
                IntegrationFingerprint = "integration",
                ActivePhysicalDisplayPathCount = 1,
                ActivePhysicalDisplayPaths = new[] { "display-path" },
                DisplayTopologyFingerprint = "display"
            };
        }

        private static void AssertThrows(
            Action action,
            string expected)
        {
            try
            {
                action();
            }
            catch (Exception failure)
            {
                if (failure.Message.IndexOf(
                    expected,
                    StringComparison.OrdinalIgnoreCase) < 0)
                {
                    throw new InvalidOperationException(
                        "Unexpected failure: " + failure.Message);
                }
                return;
            }
            throw new InvalidOperationException(
                "Expected failure containing: " + expected);
        }

        private static void AssertAnyThrows(Action action)
        {
            try
            {
                action();
            }
            catch
            {
                return;
            }
            throw new InvalidOperationException(
                "Expected operation to fail.");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void Run(string name, Action action)
        {
            try
            {
                action();
                ++passed;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception failure)
            {
                ++failed;
                Console.Error.WriteLine(
                    "FAIL " + name + ": " + failure.Message);
            }
        }
    }
}
