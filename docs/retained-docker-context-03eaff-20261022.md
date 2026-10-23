# Retained Docker context exclusions: Legacy.Maliev.QuotationService

Proposal base remote main: `0f65187787f9fa52c8ba31d1bd7a60829393fe3e`, tree `aea5c92bac71a7d091656400b82932c7b02d9b28`. This is a source-only candidate; Root owns publication, required GitHub Actions and acceptance. No build or Docker command was run by the author.

## Original owner obligation

Authoritative private ledger HEAD `e29143644cd9a24eb596af9ca81bbc47dfbba190` records original `03eaff1194c3ae2a54ceefeae31deffaff90436f`, parent `5b6d94a02a7578783e0980af10ecd36856aba024`. The owner paths are:
- `Maliev.QuotationService.Api/.dockerignore`, blob `154abec4bdfb034fc76ea90bb6196b98b0ca4923`.
- `Maliev.QuotationRequestService.Api/.dockerignore`, blob `154abec4bdfb034fc76ea90bb6196b98b0ca4923`.

Each retained file contains the same 31 recursive patterns. Current root ignore blob `a57a86c6e090914913b753d06e6eb86d1d48d7bd` contains 13 rules. Preserve that entire preimage and append 29 literally absent source patterns, producing 42 rules. Literal absence is confirmed; no unexecuted Docker baseline is described as RED.

This is an actual missing build-input behavior. Source `03dc9a1271c16e6535934445e9dd6e3f30e8fffe` XML generation/isolation is already retained and is not duplicated. The service-finder producer's opaque metadata storage is already retained and is not reimplemented here. Other owners of 03eaff remain independent; this slice awards no whole-source closure credit and makes no ledger write.

## Build boundary and regression

The actual service Dockerfile uses repository-root context. It copies .dependencies before restore; positive controls retain checked-out ServiceDefaults/CompatibilityContracts metadata and actual source inputs.
Quotation's MigrationRunner Dockerfile and its production project tree are included in the input review and positive controls.


No inspected production project embeds or publishes an excluded shell/YAML input. The test copies actual C#, project, resource, JSON, XML, props and targets files, excluding generated bin/obj and checkout metadata; exact bytes must survive. Root NuGet/props inputs and shared dependency README/global.json/NuGet/props files also survive. Existing dependency pins and lane differences are preserved.

The real-engine regression invokes the installed Docker buildx binary directly in its supported standalone mode. It admits only local Docker endpoints, removes context/plugin-command overrides in the child environment, and requires the existing default driver to be docker. No plugin-discovery subprocess chain, builder-container creation, syntax-frontend fetch, external base image, RUN, network access, runtime provider, database or financial execution is introduced. The scratch Dockerfile contains only FROM scratch and COPY, with local directory export.

The default builder is inspected using supported standalone buildx inspect default without --format or --bootstrap. The exact parser and its synthetic cases are retained from accepted Auth PR #189 head 91a8a4f2a275b2c745cd96f747904ca1ac7104f1, DockerBuildContextParityTests.cs blob 62a73e4f025fbfb7b04204b3f0baff7551d1e16e. It admits exactly one top-level Name: default and Driver: docker before the exact Nodes: boundary, refuses missing/duplicate/foreign/error/unknown headers, control characters and oversized output, and never confuses node names with the builder header. Parser cases add no workers and raw inspection output is withheld. This V4 supersedes V3, whose --format startup call was unsupported; author-side Docker execution remains not run.

Docker evaluates all 31 original categories using 69 root/nested negative sentinels and compares every admitted actual-file byte. Dockerfile.dockerignore sentinels are nested so they cannot replace the root policy under test. No custom ignore matcher substitutes for Docker. No old assertions, test data, filters, skips, collection parallelism or required checks change.

This new real-engine proof lane requires Linux /proc/meminfo and the existing default local Docker driver. Unsupported platforms fail setup; they are not skipped. No accepted Windows qualification contract exists for this new test class. Windows native validation is not run and is unsupported in this proof lane; application Windows/Aspire/runtime paths remain unchanged.

Immediately before each worker starts, the test requires one well-formed Linux /proc/meminfo MemAvailable value of at least 4194304 KiB. Missing, malformed or insufficient admission fails setup without starting a worker; no skip, retry or total-memory substitute is used. This follows CareerScaffoldRuntimeProof.cs blob 280bc63863cbfbae015a1f5af548f7fdf26e33ff lines 137-142. The ordinary workflows and their pinned dotnet-validate actions provide no per-command admission, so this check stays inside the new command helper. Exact-byte comparisons use SequenceEqual with a fixed safe assertion message; mismatched source/config bytes are withheld.

Each command has a 60 second work deadline, bounded output and bounded cleanup. The exact process handle, PID, birth time and executable are retained and checked before signals; graceful cancellation precedes exact-process escalation. No process-tree/name selector is used. Pipes and reader settlement are observed before handles close. Uncertain custody is retained with an expiry and metadata rather than signalled without proof. After exact command and reader release, the unique temporary directory is checked against its declared boundary, removed and verified absent in finally. Unresolved command custody instead retains that directory and links it to exact command run IDs and arguments; no deletion occurs while the build may still be using it. Cleanup cannot replace an original failure. Shared daemon resources are preserved; no global prune or Docker/WSL shutdown occurs.

Independent read-only review checked source owner paths, current Dockerfiles/projects, pinned shared dependency inputs and actual proposed postimages. Standalone mode is supported in Docker buildx's primary source:
https://github.com/docker/buildx/blob/v0.28.0/cmd/buildx/main.go
https://github.com/docker/buildx/blob/v0.28.0/commands/root.go

## Validation and holds

Author checks: exact source-rule transfer/prefix preservation, original blob/parent/path identity, required-input source inspection, candidate immutable readbacks and actual inverse-tree equality. Build, focused Docker test, relevant suite, formatting, static/dependency/security, contract and coverage gates are **not run** in this source-only lane; all applicable checks remain required on the exact proposed head in GitHub Actions under Root's existing financial-stop admission rules. Preserve every threshold and protection; no unrun check is a pass.

Root must first obtain Release 0 warnings / 0 errors, then run RetainedDockerBuildContextTests against real local Docker/BuildKit and inspect its admitted-file/69negative counts, followed by existing required checks and exact protected-main acceptance. No image publication/deployment, persistent data, provider/FX activation, migration execution, security configuration, dependency-pin change or SDK role is authorized by this packet. Comment/TrackingNumber remain blocked. The separate Career wire-contract producer still depends on accepted Web738.

## Official test-fixture prerequisites

The existing registry prerequisite rejected the superseded fixture indexes with
`Original Hub tag drift` before the application build. The Docker-context delivery
now refreshes only the PostgreSQL and Redis test-fixture identities and their raw
public provenance. The three admitted image aliases, registry repositories,
validation predicates, retry limits, process custody and cleanup rules stay intact.
Ryuk retains its existing immutable identity.

| Test alias | Official index SHA-256 | Linux/amd64 manifest SHA-256 |
| --- | --- | --- |
| `postgres:18-alpine` | `898c60cb390d1085bc97bdd6a1ae968757421df394b071848b52b1c09c89d6a5` | `3b0ef735388b2cb097fc29b8bb421d86d57d0ddbe7aa1fbd6493e06d65028503` |
| `redis:7-alpine` | `57e4255cc971b1c8d6f8a6ca283ac01bc3ecdd914273ef6bfaede87aaecd6541` | `8be847644e195abc4c10c8d9936cfa45332eccc6b714fa7b186ff43f1f4d5579` |

Public Docker Hub tag metadata and exact immutable ECR Public index/manifest bytes
agree on each platform, configuration digest and layer descriptor. The six `.b64`
fixtures retain those public bytes. Existing raw-provenance tests validate their
checksums and associations; the added rejection test preserves refusal of both
superseded indexes. No container or image was created during metadata observation.
Mutable upstream tags can move again; the existing drift rejection remains required.

The ordinary qualification HTTP fixture selects the same two immutable mirror
references with image pulling disabled. A cross-boundary consistency regression
checks both references against the preload inventory. Hosted application and real
HTTP/container tests remain the acceptance evidence for this fixture.
