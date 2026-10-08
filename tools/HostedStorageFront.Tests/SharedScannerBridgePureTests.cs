using System.Text.Json;
using System.Text.Json.Nodes;

namespace InvoiceCompletionProducerAcceptance.Companion;

public sealed class SharedScannerBridgePureTests
{
    [Fact]
    public void OldLeaseDefaultAndWildcardListenerContractRemainUnchanged()
    {
        var backend = BackendObservationPureTests.Lease();
        Assert.False(backend.BindOwnedIpv4);
        Assert.Equal("0.0.0.0", backend.Command[3]);
        Assert.Equal("321", ObservedStorageBackend.ListeningInode(Table("00000000"), 45002));
        Assert.Throws<InvalidDataException>(() => ObservedStorageBackend.ListeningInode(Table("0200FDF0"), 45002));
    }

    [Fact]
    public void ExactPrivateListenerUsesLinuxLittleEndianAndRejectsWildcard()
    {
        Assert.Equal("321", ObservedStorageBackend.ListeningInode(Table("02F0FD0A"), 45002, "10.253.240.2"));
        Assert.Throws<InvalidDataException>(() => ObservedStorageBackend.ListeningInode(Table("00000000"), 45002, "10.253.240.2"));
    }

    [Theory]
    [InlineData("03F0FD0A")]
    [InlineData("0100007F")]
    [InlineData("0AFDF002")]
    public void ForeignLoopbackOrWrongByteOrderCannotSatisfyPrivateBinding(string address)
    {
        Assert.Throws<InvalidDataException>(() => ObservedStorageBackend.ListeningInode(Table(address), 45002, "10.253.240.2"));
    }

    [Fact]
    public void TwoExactPrivateListenersRemainRefused()
    {
        var table = Table("02F0FD0A");
        Assert.Throws<InvalidDataException>(() => ObservedStorageBackend.ListeningInode(table + table[(table.IndexOf('\n') + 1)..], 45002, "10.253.240.2"));
    }

    [Fact]
    public void ExactTwoMemberConfiguredBridgeAndScannerSnapshotsSatisfyPureChecks()
    {
        var observation = new SharedScannerBridgeObservation(Backend(), Scanner());
        observation.ValidateDeclaration();
        using var network = Document(Network());
        using var scanner = Document(Container());
        observation.VerifyNetwork(network.RootElement);
        observation.VerifyScanner(scanner.RootElement);
        // Pure snapshots never constitute original object custody, native observation or cleanup.
    }

    [Theory]
    [InlineData("id")]
    [InlineData("created")]
    [InlineData("name")]
    [InlineData("owner")]
    [InlineData("member")]
    [InlineData("third")]
    [InlineData("gateway")]
    [InlineData("subnet")]
    [InlineData("ipv6")]
    [InlineData("external")]
    [InlineData("ingress")]
    public void BridgeIdentityCensusOrIpamMutationIsRejected(string mutation)
    {
        var value = Network();
        switch (mutation)
        {
            case "id": value["Id"] = new string('f', 64); break;
            case "created": value["Created"] = "2026-10-06T00:00:04Z"; break;
            case "name": value["Name"] = "foreign"; break;
            case "owner": value["Labels"]!["financial.acceptance.run"] = "foreign"; break;
            case "member": value["Containers"]!.AsObject().Remove(Scanner().ScannerContainerId); break;
            case "third": value["Containers"]![new string('f', 64)] = new JsonObject(); break;
            case "gateway": value["IPAM"]!["Config"]![0]!["Gateway"] = "10.253.240.4"; break;
            case "subnet": value["IPAM"]!["Config"]![0]!["Subnet"] = "10.253.240.16/28"; break;
            case "ipv6": value["EnableIPv6"] = true; break;
            case "external": value["Internal"] = false; break;
            case "ingress": value["Ingress"] = true; break;
        }
        using var document = Document(value);
        Assert.Throws<InvalidDataException>(() => new SharedScannerBridgeObservation(Backend(), Scanner()).VerifyNetwork(document.RootElement));
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("image")]
    [InlineData("created")]
    [InlineData("owner")]
    [InlineData("memory")]
    [InlineData("ip")]
    [InlineData("published")]
    [InlineData("mount")]
    public void ScannerGenerationImageCapsEndpointOrPublicationMismatchIsRejected(string mutation)
    {
        var value = Container();
        switch (mutation)
        {
            case "pid": value["State"]!["Pid"] = 222; break;
            case "image": value["Image"] = "sha256:" + new string('f', 64); break;
            case "created": value["Created"] = "2026-10-06T00:00:03Z"; break;
            case "owner": value["Config"]!["Labels"]!["financial.acceptance.run"] = "foreign"; break;
            case "memory": value["HostConfig"]!["Memory"] = 0; break;
            case "ip": value["NetworkSettings"]!["Networks"]!["owned"]!["IPAddress"] = "10.253.240.4"; break;
            case "published": value["HostConfig"]!["PublishAllPorts"] = true; break;
            case "mount": value["Mounts"]![0]!["Type"] = "volume"; break;
        }
        using var document = Document(value);
        Assert.Throws<InvalidDataException>(() => new SharedScannerBridgeObservation(Backend(), Scanner()).VerifyScanner(document.RootElement));
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("same-container")]
    [InlineData("foreign-pool")]
    [InlineData("gateway")]
    [InlineData("same-ip")]
    public void DeclarationDoesNotPromoteUnknownOrLegacyIdentityToSharedMode(string mutation)
    {
        var backend = Backend(); var scanner = Scanner();
        switch (mutation)
        {
            case "legacy": backend = backend with { BindOwnedIpv4 = false }; break;
            case "same-container": scanner = scanner with { ScannerContainerId = backend.ContainerId }; break;
            case "foreign-pool": scanner = scanner with { Subnet = "10.254.240.0/28" }; break;
            case "gateway": scanner = scanner with { Gateway = "10.253.240.4" }; break;
            case "same-ip": scanner = scanner with { ScannerBridgeIp = backend.BridgeIp }; break;
        }
        Assert.Throws<InvalidDataException>(() => new SharedScannerBridgeObservation(backend, scanner).ValidateDeclaration());
    }

    [Fact]
    public void ExistingRecordConstructorAndDeconstructShapesRemainUnchanged()
    {
        Assert.Equal("BootstrapPipeInode", typeof(FrontHostProfile).GetConstructors().Single().GetParameters().Last().Name);
        Assert.Equal("IssuedUtc", typeof(StorageBackendLease).GetConstructors().Single().GetParameters().Last().Name);
        Assert.DoesNotContain(typeof(FrontHostProfile).GetMethod("Deconstruct")!.GetParameters(), p => p.Name == "SharedScannerBridge");
        Assert.DoesNotContain(typeof(StorageBackendLease).GetMethod("Deconstruct")!.GetParameters(), p => p.Name == "BindOwnedIpv4");
        Assert.NotNull(typeof(ObservedStorageBackend).GetConstructor([typeof(StorageBackendLease)]));
        Assert.False(BackendObservationPureTests.Lease().BindOwnedIpv4);
        Assert.Null(FrontAdmissionPureTests.Profile().SharedScannerBridge);
        var json = JsonSerializer.SerializeToNode(FrontAdmissionPureTests.Profile())!.AsObject();
        Assert.False(json.ContainsKey("SharedScannerBridge"));
        Assert.False(json["Backend"]!.AsObject().ContainsKey("BindOwnedIpv4"));
    }

    [Fact]
    public void LegacyJsonWithoutOptionalFieldsAndExplicitSharedProfileRoundTrip()
    {
        var json = JsonSerializer.SerializeToNode(FrontAdmissionPureTests.Profile())!.AsObject();
        json.Remove("SharedScannerBridge"); json["Backend"]!.AsObject().Remove("BindOwnedIpv4");
        var legacy = FrontHostAdmission.Parse(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.False(legacy.Backend.BindOwnedIpv4); Assert.Null(legacy.SharedScannerBridge);
        var profile = FrontAdmissionPureTests.Profile() with { Backend = Backend(), SharedScannerBridge = Scanner() };
        var parsed = FrontHostAdmission.Parse(JsonSerializer.SerializeToUtf8Bytes(profile));
        Assert.True(parsed.Backend.BindOwnedIpv4); Assert.Equal(profile.SharedScannerBridge, parsed.SharedScannerBridge);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("missing-pid")]
    [InlineData("null-image")]
    [InlineData("integer-mode")]
    public void MalformedSharedProfileCannotCreateAnObserverContract(string mutation)
    {
        var json = JsonSerializer.SerializeToNode(FrontAdmissionPureTests.Profile() with { Backend = Backend(), SharedScannerBridge = Scanner() })!.AsObject();
        switch (mutation)
        {
            case "unknown": json["SharedScannerBridge"]!["OwnedReceipt"] = true; break;
            case "missing-pid": json["SharedScannerBridge"]!.AsObject().Remove("ScannerPid"); break;
            case "null-image": json["SharedScannerBridge"]!["ScannerImageId"] = null; break;
            case "integer-mode": json["Backend"]!["BindOwnedIpv4"] = 1; break;
        }
        Assert.Throws<JsonException>(() => FrontHostAdmission.Parse(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("foreign-prefix")]
    [InlineData("missing-pin")]
    [InlineData("foreign-label")]
    [InlineData("volume")]
    public void ActualImageSnapshotsRequirePinnedBaseAndOneOwnedDerivedLayer(string mutation)
    {
        string first = "sha256:" + new string('a', 64), second = "sha256:" + new string('b', 64);
        var original = new JsonObject { ["Id"] = "sha256:" + new string('d', 64), ["RepoDigests"] = new JsonArray(SharedScannerBridgeObservation.BaseImage), ["RootFS"] = new JsonObject { ["Layers"] = new JsonArray(first) } };
        var derived = new JsonObject { ["Id"] = Scanner().ScannerImageId, ["RootFS"] = new JsonObject { ["Layers"] = new JsonArray(first, second) }, ["Config"] = new JsonObject { ["Labels"] = new JsonObject { ["financial.acceptance.run"] = Backend().LeaseId[5..] } } };
        switch (mutation)
        {
            case "foreign-prefix": derived["RootFS"]!["Layers"]![0] = second; break;
            case "missing-pin": original["RepoDigests"] = new JsonArray("foreign"); break;
            case "foreign-label": derived["Config"]!["Labels"]!["financial.acceptance.run"] = "foreign"; break;
            case "volume": derived["Config"]!["Volumes"] = new JsonObject { ["/data"] = new JsonObject() }; break;
        }
        using var baseDoc = Document(original); using var derivedDoc = Document(derived);
        var observation = new SharedScannerBridgeObservation(Backend(), Scanner());
        if (mutation == "valid") observation.VerifyImageChain(baseDoc.RootElement, derivedDoc.RootElement);
        else Assert.Throws<InvalidDataException>(() => observation.VerifyImageChain(baseDoc.RootElement, derivedDoc.RootElement));
    }

    private static StorageBackendLease Backend()
    {
        var lease = BackendObservationPureTests.Lease();
        var command = (string[])lease.Command.Clone(); command[3] = "10.253.240.2";
        command[9] = "http://10.253.240.2:45002"; command[11] = "10.253.240.2:45002";
        return lease with { BindOwnedIpv4 = true, BridgeIp = "10.253.240.2", Command = command };
    }

    private static SharedScannerBridgeLease Scanner() => new(new string('e', 64),
        "2026-10-06T00:00:01.123456789Z", "2026-10-06T00:00:02.123456789Z", 123,
        "sha256:" + new string('e', 64), "10.253.240.3", "financial-scanner-" + Backend().LeaseId[5..] + "-network",
        "10.253.240.0/28", "10.253.240.1");

    private static JsonObject Network() => new()
    {
        ["Id"] = Backend().NetworkId,
        ["Created"] = Backend().NetworkCreated,
        ["Name"] = Scanner().NetworkName,
        ["Driver"] = "bridge",
        ["Internal"] = true,
        ["EnableIPv6"] = false,
        ["Scope"] = "local",
        ["Ingress"] = false,
        ["Attachable"] = false,
        ["Labels"] = new JsonObject { ["financial.acceptance.run"] = Backend().LeaseId[5..] },
        ["Containers"] = new JsonObject { [Backend().ContainerId] = new JsonObject(), [Scanner().ScannerContainerId] = new JsonObject() },
        ["IPAM"] = new JsonObject { ["Driver"] = "default", ["Options"] = new JsonObject(), ["Config"] = new JsonArray(new JsonObject { ["Subnet"] = Scanner().Subnet, ["Gateway"] = Scanner().Gateway }) },
    };

    private static JsonObject Container() => new()
    {
        ["Id"] = Scanner().ScannerContainerId,
        ["Created"] = Scanner().ScannerCreated,
        ["Image"] = Scanner().ScannerImageId,
        ["RestartCount"] = 0,
        ["State"] = new JsonObject { ["Running"] = true, ["Paused"] = false, ["Restarting"] = false, ["Pid"] = Scanner().ScannerPid, ["StartedAt"] = Scanner().ScannerStarted },
        ["Config"] = new JsonObject { ["Image"] = Scanner().ScannerImageId, ["Labels"] = new JsonObject { ["financial.acceptance.run"] = Backend().LeaseId[5..] }, ["Entrypoint"] = new JsonArray("/usr/sbin/clamd"), ["Cmd"] = new JsonArray("--foreground", "--config-file=/etc/clamav/acceptance.conf") },
        ["HostConfig"] = new JsonObject { ["NetworkMode"] = Backend().NetworkId, ["ReadonlyRootfs"] = true, ["Privileged"] = false, ["Memory"] = 1536L * 1024 * 1024, ["NanoCpus"] = 2000000000, ["CapDrop"] = new JsonArray("ALL"), ["Devices"] = new JsonArray(), ["PublishAllPorts"] = false, ["PidMode"] = "", ["IpcMode"] = "private", ["CgroupnsMode"] = "private", ["SecurityOpt"] = new JsonArray("no-new-privileges"), ["RestartPolicy"] = new JsonObject { ["Name"] = "no" }, ["AutoRemove"] = false, ["Tmpfs"] = new JsonObject { ["/tmp"] = "rw,nosuid,nodev,size=32m", ["/run"] = "rw,nosuid,nodev,size=4m" } },
        ["Mounts"] = new JsonArray(new JsonObject { ["Type"] = "tmpfs", ["Destination"] = "/tmp" }, new JsonObject { ["Type"] = "tmpfs", ["Destination"] = "/run" }),
        ["NetworkSettings"] = new JsonObject { ["Ports"] = new JsonObject { ["3310/tcp"] = null }, ["Networks"] = new JsonObject { ["owned"] = new JsonObject { ["NetworkID"] = Backend().NetworkId, ["IPAddress"] = Scanner().ScannerBridgeIp } } },
    };

    private static JsonDocument Document(JsonObject value) => JsonDocument.Parse(new JsonArray(value).ToJsonString());
    private static string Table(string address) => "sl local_address rem_address st tx_queue rx_queue tr tm retr uid inode\n" + $"0: {address}:AFCA 00000000:0000 0A 0 0 0 0 0 321\n";
}
