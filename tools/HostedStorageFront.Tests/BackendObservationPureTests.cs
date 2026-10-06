using System.Text.Json;
using System.Text.Json.Nodes;

namespace InvoiceCompletionProducerAcceptance.Companion;

public sealed class BackendObservationPureTests
{
    [Fact]
    public void ExactFrontRunAndLeaseBindToTheBackend()
    {
        var lease = Lease();
        new ObservedStorageBackend(lease).RequireLeaseBinding(lease.RunId, lease.ExpiresUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferentFrontRunOrExpiryCannotReuseAnObservedBackend(bool differentRun)
    {
        var lease = Lease();
        var backend = new ObservedStorageBackend(lease);
        Assert.Throws<InvalidDataException>(() => backend.RequireLeaseBinding(
            differentRun ? "c821-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" : lease.RunId,
            differentRun ? lease.ExpiresUtc : lease.ExpiresUtc.AddSeconds(1)));
    }

    [Theory]
    [InlineData("2026-10-06T00:00:00Z", 0)]
    [InlineData("2026-10-06T00:00:00.1Z", 1000000)]
    [InlineData("2026-10-06T00:00:00.123456789Z", 1234567)]
    public void DockerNanosecondsAreBoundedToClrTickPrecision(string text, long ticks)
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero).AddTicks(ticks), ObservedStorageBackend.DockerInstant(text));
    }

    [Theory]
    [InlineData("2026-10-06T00:00:00+00:00")]
    [InlineData("2026-10-06T00:00:00.1234567890Z")]
    [InlineData("2026-10-06 00:00:00Z")]
    public void DockerGenerationRequiresExactUtcShape(string text)
    {
        Assert.Throws<InvalidDataException>(() => ObservedStorageBackend.DockerInstant(text));
    }

    [Fact]
    public void BackendInitKernelGenerationIsReadFromField22()
    {
        Assert.Equal(4567L, ObservedStorageBackend.KernelStart(FrontAdmissionPureTests.Stat("1 (fake-gcs (server))", "S", "0", "4567")));
    }

    [Theory]
    [InlineData("2 (server)", "4567")]
    [InlineData("1 (server)", "0")]
    [InlineData("1 (server)", "-1")]
    public void DifferentOrMissingInitGenerationIsRejected(string prefix, string ticks)
    {
        Assert.Throws<InvalidDataException>(() => ObservedStorageBackend.KernelStart(FrontAdmissionPureTests.Stat(prefix, "S", "0", ticks)));
    }

    [Fact]
    public void OneActualWildcardListenerReturnsItsSocketInode()
    {
        Assert.Equal("321", ObservedStorageBackend.ListeningInode(Table("00000000", "0A", "321"), 45002));
    }

    [Theory]
    [InlineData("0100007F", "0A", "321")]
    [InlineData("00000000", "01", "321")]
    [InlineData("00000000", "0A", "0")]
    [InlineData("00000000", "0A", "invalid")]
    public void WrongAddressStateOrInodeCannotEstablishListenerOwnership(string address, string state, string inode)
    {
        Assert.Throws<InvalidDataException>(() => ObservedStorageBackend.ListeningInode(Table(address, state, inode), 45002));
    }

    [Fact]
    public void DuplicateListenerIsRejected()
    {
        string table = Table("00000000", "0A", "321");
        Assert.Throws<InvalidDataException>(() => ObservedStorageBackend.ListeningInode(table + table[(table.IndexOf('\n') + 1)..], 45002));
    }

    [Fact]
    public void ExactContainerAndNetworkSnapshotsSatisfyPureIdentityChecks()
    {
        var actual = new ObservedStorageBackend(Lease());
        using var container = Document(Container());
        using var network = Document(Network());
        actual.VerifyContainer(container.RootElement);
        actual.VerifyNetwork(network.RootElement);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("created")]
    [InlineData("started")]
    [InlineData("image")]
    [InlineData("owner")]
    [InlineData("stopped")]
    [InlineData("restart")]
    [InlineData("privileged")]
    [InlineData("writable")]
    [InlineData("memory")]
    [InlineData("cpu")]
    [InlineData("caps")]
    [InlineData("mount")]
    [InlineData("published")]
    [InlineData("host-pid")]
    [InlineData("host-ipc")]
    [InlineData("autoremove")]
    [InlineData("bridge-ip")]
    [InlineData("argv")]
    public void ContainerSnapshotMismatchIsRejected(string mutation)
    {
        var json = Container();
        var config = json["Config"]!.AsObject();
        var host = json["HostConfig"]!.AsObject();
        var state = json["State"]!.AsObject();
        switch (mutation)
        {
            case "id": json["Id"] = new string('f', 64); break;
            case "created": json["Created"] = "2026-10-06T00:00:02Z"; break;
            case "started": state["StartedAt"] = "2026-10-06T00:00:03Z"; break;
            case "image": json["Image"] = "sha256:" + new string('f', 64); break;
            case "owner": config["Labels"]!["com.maliev.c821.run"] = "other"; break;
            case "stopped": state["Running"] = false; break;
            case "restart": json["RestartCount"] = 1; break;
            case "privileged": host["Privileged"] = true; break;
            case "writable": host["ReadonlyRootfs"] = false; break;
            case "memory": host["Memory"] = 0; break;
            case "cpu": host["NanoCpus"] = 0; break;
            case "caps": host["CapAdd"] = new JsonArray("SYS_ADMIN"); break;
            case "mount": json["Mounts"] = new JsonArray(new JsonObject { ["Type"] = "volume" }); break;
            case "published": json["NetworkSettings"]!["Ports"]!["4443/tcp"] = new JsonArray(new JsonObject { ["HostPort"] = "45002" }); break;
            case "host-pid": host["PidMode"] = "host"; break;
            case "host-ipc": host["IpcMode"] = "host"; break;
            case "autoremove": host["AutoRemove"] = true; break;
            case "bridge-ip": json["NetworkSettings"]!["Networks"]!["owned"]!["IPAddress"] = "127.0.0.1"; break;
            case "argv": config["Cmd"] = new JsonArray("-backend", "filesystem"); break;
        }
        using var document = Document(json);
        Assert.Throws<InvalidDataException>(() => new ObservedStorageBackend(Lease()).VerifyContainer(document.RootElement));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("external")]
    [InlineData("ipv6")]
    [InlineData("driver")]
    [InlineData("scope")]
    [InlineData("created")]
    [InlineData("owner")]
    [InlineData("foreign-member")]
    public void NetworkSnapshotMismatchIsRejected(string mutation)
    {
        var json = Network();
        switch (mutation)
        {
            case "id": json["Id"] = new string('f', 64); break;
            case "external": json["Internal"] = false; break;
            case "ipv6": json["EnableIPv6"] = true; break;
            case "driver": json["Driver"] = "host"; break;
            case "scope": json["Scope"] = "swarm"; break;
            case "created": json["Created"] = "2026-10-06T00:00:02Z"; break;
            case "owner": json["Labels"]!["com.maliev.c821.lease"] = "other"; break;
            case "foreign-member": json["Containers"]![new string('f', 64)] = new JsonObject(); break;
        }
        using var document = Document(json);
        Assert.Throws<InvalidDataException>(() => new ObservedStorageBackend(Lease()).VerifyNetwork(document.RootElement));
    }

    internal static StorageBackendLease Lease()
    {
        const string run = "c821-11111111-2222-3333-4444-555555555555";
        return new(new string('a', 64), new string('b', 64), ObservedStorageBackend.PinnedImage,
            ObservedStorageBackend.PinnedConfig, "2026-10-06T00:00:01.123456789Z", "2026-10-06T00:00:02.123456789Z", "172.18.0.2", 45002,
            "/bin/fake-gcs-server", new string('c', 64), ["/bin/fake-gcs-server"],
            ["-scheme", "http", "-host", "0.0.0.0", "-port", "45002", "-backend", "memory", "-external-url", "http://172.18.0.2:45002", "-public-host", "172.18.0.2:45002"],
            run, run, "12345", "1", new DateTimeOffset(2026, 10, 6, 0, 20, 0, TimeSpan.Zero), 268435456, 1000000000, 4567,
            new string('d', 40), "2026-10-06T00:20:00Z", "2026-10-06T00:00:00.123456789Z", new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));
    }

    private static JsonObject Labels()
    {
        var lease = Lease();
        return new JsonObject
        {
            ["com.maliev.c821.run"] = lease.GithubRunId,
            ["com.maliev.c821.attempt"] = lease.GithubAttempt,
            ["com.maliev.c821.file-source"] = lease.FileSourceSha,
            ["com.maliev.c821.lease"] = lease.LeaseId,
            ["com.maliev.c821.expires"] = lease.ExpiresText,
            ["com.maliev.c821.persistent"] = "false",
            ["com.maliev.c821.role"] = "storage",
        };
    }

    private static JsonObject Container()
    {
        var lease = Lease();
        return new JsonObject
        {
            ["Id"] = lease.ContainerId,
            ["Created"] = lease.Created,
            ["Image"] = lease.ImageId,
            ["RestartCount"] = 0,
            ["State"] = new JsonObject { ["Running"] = true, ["Paused"] = false, ["Restarting"] = false, ["StartedAt"] = lease.Started, ["Pid"] = 123 },
            ["Config"] = new JsonObject { ["Image"] = lease.ImageReference, ["Labels"] = Labels(), ["Entrypoint"] = JsonSerializer.SerializeToNode(lease.Entrypoint), ["Cmd"] = JsonSerializer.SerializeToNode(lease.Command) },
            ["HostConfig"] = new JsonObject
            {
                ["NetworkMode"] = lease.NetworkId,
                ["Privileged"] = false,
                ["ReadonlyRootfs"] = true,
                ["RestartPolicy"] = new JsonObject { ["Name"] = "no" },
                ["Memory"] = lease.MemoryLimitBytes,
                ["NanoCpus"] = lease.NanoCpus,
                ["CapDrop"] = new JsonArray("ALL"),
                ["CapAdd"] = null,
                ["Binds"] = null,
                ["Devices"] = new JsonArray(),
                ["PortBindings"] = new JsonObject(),
                ["PidMode"] = "",
                ["IpcMode"] = "private",
                ["CgroupnsMode"] = "private",
                ["AutoRemove"] = false,
                ["Tmpfs"] = null,
                ["SecurityOpt"] = new JsonArray("no-new-privileges:true"),
            },
            ["Mounts"] = new JsonArray(),
            ["NetworkSettings"] = new JsonObject
            {
                ["Ports"] = new JsonObject { ["4443/tcp"] = null },
                ["Networks"] = new JsonObject { ["owned"] = new JsonObject { ["NetworkID"] = lease.NetworkId, ["IPAddress"] = lease.BridgeIp } },
            },
        };
    }

    private static JsonObject Network() => new()
    {
        ["Id"] = Lease().NetworkId,
        ["Driver"] = "bridge",
        ["Internal"] = true,
        ["EnableIPv6"] = false,
        ["Scope"] = "local",
        ["Created"] = Lease().NetworkCreated,
        ["Labels"] = Labels(),
        ["Containers"] = new JsonObject { [Lease().ContainerId] = new JsonObject() },
    };

    private static JsonDocument Document(JsonObject item) => JsonDocument.Parse(new JsonArray(item).ToJsonString());
    private static string Table(string address, string state, string inode) =>
        " sl local_address rem_address st tx_queue rx_queue tr tm retr uid inode\n" + $"0: {address}:AFCA 00000000:0000 {state} 0 0 0 0 0 {inode}\n";
}
