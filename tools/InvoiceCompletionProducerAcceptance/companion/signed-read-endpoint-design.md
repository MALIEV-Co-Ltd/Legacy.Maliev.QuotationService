# Dedicated storage endpoint proposal - source review only

No endpoint has been started, no image selected, and no signed URL has been exercised.
`HostedV4SignedReadVerifier.cs` is proposed source, not compiled or connected middleware.
It uses framework RSA SPKI import and PKCS#1 SHA-256 verification. It is not a storage adapter.
Native tests use Google.Cloud.Storage.V1 5.0.0, aligned with the current File SDK, and include
the signed response-content-disposition query. SDK canonical Host omits Options.WithPort;
the verifier follows that SDK signature convention and separately pins full request authority.

## Composition and actual authorization boundary

One digest-pinned, run-owned storage container should contain an owned HTTP front and the
real fake-gcs-server protocol backend. Only the front's explicit port is published to host
loopback; backend binds container loopback and has no publication or shared network clients.
Both exact commands, binary hashes, listening process generations and consumed endpoint
configuration require independent observation. This is an additional gate; the current
generic storage container observer does not yet implement front/backend process admission.
Backend image/version/database behavior must be selected and reviewed before activation.

File uses its real Google StorageClient against that front for JSON API metadata and upload
operations, with canonical bucket `maliev.com` confined to disposable storage. Actual SDK
upload bytes, backend-issued object generation, metadata and signed download must be retained
and compared. No generated generation or mirrored in-memory dictionary is evidence.

Before any XML GET `/maliev.com/invoices/...` is proxied, the front must call Authorize on
the actual HTTP method, single actual Host header and ASP.NET RawTarget. It must reject extra
Host values, Authorization, x-goog/x-amz headers, body, transfer encoding, duplicate query
parameters, unsupported routes and unsigned requests before contacting the backend. Routing
must not normalize/rewrite the raw resource path before verification or before forwarding.
The backend path/query is the exact verified target. A forged, expired or uninitialized
signature produces an empty 403/503 and zero backend reads, never an emulator fallback.

Public SPKI reaches the front only after the launcher's authenticated signing-key handshake
has independently observed the exact File process before and after the read. Bootstrap is
an owner-held IPC pipe in the storage container, with a one-time public-key message and
public fingerprint acknowledgment. No HTTP key-registration route or private key is allowed.
Until bootstrap finishes the XML route fails closed; existing SDK JSON writes need no signer
key and use only the isolated run-owned backend. The proposed verifier pins the currently
observed File authorizer literal and only host-signed GETs, optionally with signed generation.

The front and backend clients must use fixed loopback origins, no redirects or proxy, bounded
headers/bodies/response streams and per-request cancellation limited by the same run expiry.
Any resumable upload Location is accepted only for the exact known backend origin/session
route and rewritten to the exact front origin, preserving the opaque session target. Arbitrary
redirects, backend/public origin escapes and body processing after lease expiry are failures.
The concrete proxy/caller and IPC implementation remain required source work.

## Native evidence required before accepting this composition

The hosted gate must run the actual reviewed File SDK upload and URL signing and issue an
ordinary HTTP GET to this front with the returned URL. Require exact PDF bytes/hash and
backend-issued generation; then mutate signature, path, generation, host, date and expiry
individually and require 403 with zero backend reads. Include expired valid signatures and
wrong public keys, unsigned reads, duplicate queries and expiry during response copying.
Native verifier tests must use the real Google UrlSigner with an ephemeral owned RSA key,
not a duplicate canonicalization helper as the successful oracle. A dedicated short-duration
UrlSigner control can prove expiry while keeping File's emitted invoice URL unchanged.

File restart/remint requires a new independently observed File generation and a fresh
storage-front instance/public-key bootstrap; old-key URLs must reject. Driver replay then
checks persisted File object identity, one InvoiceFile and stable completion against actual
normal service readbacks. The outer owner closes all eight hosts and its client connections
before independently verifying unused container ports and removing only exact owned resources.

## Primary protocol references

- https://cloud.google.com/storage/docs/authentication/canonical-requests
- https://cloud.google.com/storage/docs/authentication/signatures
- https://github.com/googleapis/google-cloud-dotnet/blob/main/apis/Google.Cloud.Storage.V1/Google.Cloud.Storage.V1/UrlSigner.V4Signer.cs
- https://github.com/fsouza/fake-gcs-server#using-with-signed-urls

The emulator alone does not enforce signature or expiry. Google defines the canonical method,
resource path, sorted query excluding signature, host header and UNSIGNED-PAYLOAD; its
string-to-sign uses algorithm, date, credential scope and canonical-request SHA-256. This
dedicated profile conservatively requires signedAt <= now < expiry and the independent
run lease, rather than relying on provider clock-skew allowances.
