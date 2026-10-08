# Fixed-image oracle staging correction

Run37738972185 at head6cf5572b reported oracle-read/source-guard/guard-0e8029404a4a.
That exact composite guard checks the preopened original FD, pathname identity,
regular-file type and finite nonempty size. Its exact failing subpredicate and
installed Docker version were not emitted. DockerCLI v28 copyFromContainer calls
Moby CopyTo/Untar; archive.Unpack removes/recreates an existing non-directory
destination. Six pure models of the exact published guard reproduce refusal of
the replaced pathname and empty held FD, without claiming a native reproduction.

The original target is still allocated O_EXCL and its original identity predicate
is unchanged. DockerCP now writes only a separately declared absent staging path
inside the original private0700 parent. Original CLI cleanup/success precedes
nofollow/nonblocking acquisition of its regular one-link finite staging file.
Bounded reads and progressing writes transfer bytes into the ORIGINAL descriptor.
The copy source remains the independently pinned NEVERSTARTED image oracle, not
the later live backend. Image/layer/cap/owner fences remain required.

Stage and original descriptors/paths have independent cleanup attempts. Missing
acquired staging identity is not permission to adopt/delete an uncertain path;
it retains the original owner. Close ambiguity cannot authorize repeated closes.
No cancelled runner cleanup, File/frontend or eight-host acceptance is inferred.

Primary source links:
- https://github.com/docker/cli/blob/v28.0.0/cli/command/container/cp.go
- https://github.com/moby/moby/blob/v28.0.0/pkg/archive/copy.go#L388-L406
- https://github.com/moby/moby/blob/v28.0.0/pkg/archive/archive.go#L1110-L1151
