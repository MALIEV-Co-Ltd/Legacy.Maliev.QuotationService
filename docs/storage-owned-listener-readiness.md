# Original owned listener readiness

PR141 review comment4220368236 identifies that Docker start is not listener readiness.
The retained launcher now calls a source-owned pre-observation wait before the
unchanged definitive observer. Generic Lease defaults and C# contracts are unchanged.

Only zero exact private IPv4 selected-port LISTEN inodes under verified current
original ownership may wait. Two polls each recheck immutable image, exact container,
network/scanner generations, census/IPAM/isolation limits, executable/cmdline and
kernel ticks. A positive listener also requires its original PID1 descriptor inode.
Wrong/multiple/unowned listeners, command faults, cancellation and uncertain
generation escape immediately; no general AdmissionError is retried.

Monotonic time is sampled before UTC lease remaining. Work is min(10 seconds,
remaining lease minus 30 seconds reserve). The second slot is halfway through this
work budget, capped at five seconds; early wakes recheck the absolute target.
Every direct command uses the same absolute work deadline with the existing
timeout clamp, no upward floor. There are at most two polls and 32 direct commands.
Original runner mandatory cleanup may extend past expired work within its existing
five-second fence. This does not assert a known job budget or total cleanup duration.
The existing ledger hard cap256 remains; additional observations add actual rows.

False after two verified zeros is not admission: the caller still executes the
unchanged definitive observer, retaining its first zero refusal and evidence route.
The wait mints no receipt and does not clear history, release ownership or admit
TCP6. Existing cleanup and sticky owner failure handlers remain unchanged.

Predecessor f277 actual standalone run37793216488 was separately accepted;
this new source requires its own hosted qualification. Actor-free models are not
native evidence. File/front lifecycle, C# compilation and financial eight remain
unaccepted by this packet. No production data, IAM or producer files change.
