# Built-in present-null IPAM census interoperability

Actual7a81 pair37777897634 reached the PRESENTNULL Config refusal. Its earlier
dict/key guards passed. Driver, name and scope were not observed in that receipt.
This source correction does not assert the failing network was built-in.

Only an already inspected exact-ID object with PRESENTNULL Config can normalize
its local census value to an empty list, and only when exact builtin strings are
Driver=host, Name=host, Scope=local or Driver=null, Name=none, Scope=local.
Raw inspect metadata remains unchanged. The network still counts against census
limits, is inspected and participates in the final exact-ID relist. All list
forms retain their original checks; missing/malformed Config and unknown/cross-
paired/non-local null forms refuse. No foreign network is mutated or removed.

Version-qualified primary source contract (not installed Engine version evidence):

- [Special drivers skip IPAM allocation](https://github.com/moby/moby/blob/v28.5.1/libnetwork/network.go#L1396), with [exact host/null classification](https://github.com/moby/moby/blob/v28.5.1/libnetwork/network.go#L1811).
- [Predefined none/null and host/host creation](https://github.com/moby/moby/blob/v28.5.1/daemon/daemon_unix.go#L817).
- [Host local singleton built-in driver](https://github.com/moby/moby/blob/v28.5.1/libnetwork/drivers/host/host.go#L18) and [null local singleton built-in driver](https://github.com/moby/moby/blob/v28.5.1/libnetwork/drivers/null/null.go#L18).
- [Nil IPAM config assembly](https://github.com/moby/moby/blob/v28.5.1/daemon/network.go#L658) and [exported Config slice without omitempty](https://github.com/moby/moby/blob/v28.5.1/api/types/network/ipam.go#L10).

Default Scanner branch, owned-runner, relay history/deadlines, configured census
caps/deadline, finite CIDR selection, one atomic Engine create, immediate owned
generation/IPAM inspection and independent generation-fenced cleanup are retained.
Models are actor-free. Actual configured allocation, pair cleanup, File/front and
eight-host financial acceptance remain false pending a fresh exact-head run.
