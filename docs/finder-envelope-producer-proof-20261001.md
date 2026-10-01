# Finder envelope: disposable PostgreSQL producer proof

Supports Intranet issue [241](https://github.com/MALIEV-Co-Ltd/Legacy.Maliev.Intranet/issues/241).
Exclusive QuotationService baseline: `e0a24ea08b3681fb30e2de9608cd825dd708c0ca`.
Branch: `codex/quotation-finder-producer-20261001`.
Allowed changes are exclusively the new
`Legacy.Maliev.QuotationService.Tests/Controllers/QuotationRequestFinderEnvelopeHttpTests.cs`
and this document. No existing assertion or fixture is modified.

## Independent provenance and owner dispositions

Source objects remain read-only at frozen checkpoint
`bed10c7d15e0698e0b75f1329d0f312937f5d77f`.

| Full source SHA | Parent | Subject | Quotation owner paths and retained disposition |
| --- | --- | --- | --- |
| `9b8146c4ac78397959fe1689daf96f400cfd45e8` | `43ec8aeafe977e918693771de7254867da81d0b7` | feat(web): track persisted service finder journeys | `Maliev.QuotationRequestService.Common/ServiceFinderMetadataEnvelope.cs`: pending/migration-required; this test proves opaque storage, not complete owner migration. |
| `476611058a0aba44177c98efceae2d22f8aa7858` | `0afa6ad9105f794315ab88f414870f338a838c19` | feat(web): contextualize service finder options | Same helper path: pending/migration-required; physical-part ID independently retained in fixture. |
| `2b35ab97bc656d6cae64c6aa0288c18f7b2f4bbd` | `476611058a0aba44177c98efceae2d22f8aa7858` | feat(web): add adaptive service finder refinements | Same helper path plus `Maliev.QuotationRequestService.Common/Maliev.QuotationRequestService.Common.xml`: separately pending/migration-required; optional IDs covered, generated-artifact disposition still requires approval. |
| `b7539baa75d26d1ee0e1471c413005091137dae8` | `4dea8bfe6b37a251f17a6715276f81aba339394e` | chore(repo): track deployment and XML documentation updates | Common XML path only: separately pending/migration-required; compiler output is not restored. |

Other source owners and Web paths remain independently unresolved. No ledger was
edited, and no complete source commit is claimed migrated by this test slice.

## Actual boundary, not controlled Intranet wire substitution

`RequestTriageFixture` in existing `Controllers/QuotationRequestTriageHttpTests.cs`
starts actual disposable `postgres:18-alpine` and `redis:7-alpine`, migrates separate
Quotation/QuotationRequest databases, and creates Production WebApplicationFactory
hosts. Normal RS256 JWT validation, permission middleware, actual IAM client,
repository, EF Core and Npgsql execute; only remote IAM transport is controlled.
The fixture's existing permissions remain read/update/delete.

`RequestCreateRetryFixture` in existing
`Controllers/QuotationRequestCreateRetryHttpTests.cs` supplies the same real
database/provider pipeline for POST with unchanged create-only JWT and controlled
remote IAM transport. No admission/session/authority grant is added or broadened.

Routes exercised are `POST /quotationrequests` and authenticated
`GET`/`PUT /quotationrequests/{id}`. PascalCase response fields are asserted from
actual HTTP JSON. `X-Expected-Modified-Date` comes from the loaded detail response,
using the existing explicit UTC convention for PostgreSQL unspecified timestamps;
no local-time conversion or fresh-version fetch precedes the update.

`Application/Models/QuotationModels.cs` retains InternalComment as nullable string.
`Data/QuotationRepositories.cs` maps it verbatim and projects it back; PostgreSQL
stores nullable text. Generic update mapping excludes JourneyId, TransactionId,
CreatedDate and qualification projection/history. ModifiedDate remains the
existing optimistic concurrency token, not a new column or xmin change.

## Five added tests

1. Two GET/update/readback variants: original `files-3d` required fields and
   `files-real-part` with `performance-strength`/`environment-outdoor`. Both include
   unknown root and nested answer extensions, Thai/English notes and HTML-like
   literal text. Exact string equality proves no inner JSON reserialization,
   stripping, translation or extension loss across HTTP and fresh PostgreSQL reads.
2. Updates change only the operator-note content supplied in the envelope. Forged
   update JourneyId/TransactionId/CreatedDate/qualification properties cannot
   replace server-owned attribution or immutable history. All non-note JSON
   properties are independently compared, and qualification audit rows remain exact.
3. One stale-after-winning-write case requires 204 for the first update, then 409
   using the originally loaded version; the complete winning database row,
   envelope, ModifiedDate, attribution and history remain unchanged.
4. Two actual POST variants assert 201 response, Location and fresh PostgreSQL
   exact envelope equality. Creation intentionally accepts the admitted JourneyId
   input; TransactionId is generated as `request-{Id}`, not the forged body value.

Envelope expectations use independent literal source IDs, not the Intranet
helper or any implementation-under-test generator. No API/helper behavior is
introduced into QuotationService, and malformed-envelope parsing is not added
to this intentionally opaque string storage boundary.

## Isolation and executed initial gates

Private independent `--no-hardlinks --no-checkout` clones are ignored under this
worktree's `.dependencies`. Exact existing CI validation pins:

- ServiceDefaults: `8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3`.
- CompatibilityContracts: `78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7`.
- Accounting test dependency: `ae0826156b06c34476e95de8c53dfccfcf5a5972`.

No sibling output or shared checkout is built or changed. Accounting is solely
the existing test project's pinned compile dependency, not an implementation lane.

Baseline Release build reported **0 warnings / 0 errors**. Six existing PostgreSQL
HTTP controls passed before test edits (`TestResults/finder-producer-baseline`).
An initial new test raw-string interpolation failed compilation with CS9007;
corrected only test syntax before executing behavior. This was not a runtime RED.
Final post-test solution Release build reported **0 warnings / 0 errors**.
All **5 new cases passed / 0 failed / 0 skipped**
(`TestResults/finder-producer-focused/finder-focused.trx`), establishing green
equivalence rather than a manufactured failing behavior or runtime repair.

```powershell
dotnet build Legacy.Maliev.QuotationService.slnx -c Release --no-restore -p:MalievWorkspaceRoot=B:\maliev-legacy\.worktrees\quotation-finder-producer-20261001\.dependencies -p:UseLocalMalievDependencies=true --nologo
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~QuotationRequestFinderEnvelope --logger "trx;LogFileName=finder-focused.trx" --results-directory TestResults/finder-producer-focused --nologo
```

## Final local gates and freeze

The final unfiltered affected Tests project passed **540 / 540 tests, 0 failures,
0 skipped**, in 2m27s. Actual PostgreSQL/Redis containers were disposable existing
fixtures, not a persistent database or hosted environment. Results:
`TestResults/finder-producer-full/finder-full.trx`; coverage:
`TestResults/finder-producer-full/a5574c31-948f-4e87-b878-2a448ff99a26/coverage.cobertura.xml`.

```powershell
dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj -c Release --no-build --no-restore --collect:"XPlat Code Coverage" --logger "trx;LogFileName=finder-full.trx" --results-directory TestResults/finder-producer-full --nologo
$env:PYTHONDONTWRITEBYTECODE='1'
python scripts/check_owned_coverage.py TestResults/finder-producer-full/a5574c31-948f-4e87-b878-2a448ff99a26/coverage.cobertura.xml --minimum 80
python -m unittest discover -s scripts/tests -p test_*.py
$env:MalievWorkspaceRoot='B:\maliev-legacy\.worktrees\quotation-finder-producer-20261001\.dependencies'
$env:UseLocalMalievDependencies='true'
dotnet format Legacy.Maliev.QuotationService.slnx --no-restore --verify-no-changes --include Legacy.Maliev.QuotationService.Tests/Controllers/QuotationRequestFinderEnvelopeHttpTests.cs --verbosity quiet
dotnet list Legacy.Maliev.QuotationService.slnx package --vulnerable --include-transitive --no-restore
gitleaks dir Legacy.Maliev.QuotationService.Tests/Controllers/QuotationRequestFinderEnvelopeHttpTests.cs --no-banner --redact
gitleaks dir docs/finder-envelope-producer-proof-20261001.md --no-banner --redact
```

The owned handwritten coverage gate passed at **5,206 / 5,490 = 94.83%** (80%
minimum). Owned assembly coverage: API 92.11%, Application 96.32%, Data 97.34%,
Domain 96.97%, MigrationRunner 81.24%. External pinned dependencies and generated
code are excluded by the existing unchanged gate, not new test configuration.
All five existing coverage-validator self-tests passed; their deliberate
synthetic below-threshold message tests gate failure and is not a real coverage
failure. Their initially generated two Python bytecode cache directories were
verified inside this owned worktree, containing only the newly generated `.pyc`
files, then removed. The final repository status contains only the two authorized
new files; private dependency checkouts remain clean at their exact pins.

Targeted full formatting verification passed (exit 0); package vulnerability audit
reported no vulnerable direct/transitive packages in all six solution projects
(exit 0). Secret scans found no leaks in the two owned files. `git diff --exit-code`
confirmed no tracked-file changes, and new-file `git diff --no-index --check`
found no whitespace defects (its ordinary added-file difference exit is not a
whitespace failure). The repository's CRLF-to-LF normalization notice is expected.

This is green runtime equivalence proof, not a runtime fix. The existing fixtures,
auth admission, normal retry strategies, old assertions and source objects were
left untouched. Implementation is frozen for root review.

No commit/push/deployment, persistent database, schema, runtime, API, auth,
project, workflow, old-test or ledger change is authorized. Actual integrated
browser acceptance and whole-source-owner disposition remain separate gates;
neither these PostgreSQL tests nor Intranet controlled transport proves that
complete cross-repository browser journey.

## Independent integration-owner verification

The root independently rebuilt the complete solution in Release with warnings
as errors (0 warnings/errors), executed all 5 focused cases, and reran the entire
affected suite: **540 passed, 0 failed, 0 skipped**, 2m28s. Its distinct report is
`TestResults/root-full540/root-full540.trx`, SHA-256
`5057A6AC6D265EF5CBBEADC8F7F1C156085B798820CD1A69AA7926ACE1D4BF74`.
Scoped verify-only formatting, all-six-project transitive vulnerability audit,
and unmodified redacted secret scans of both owned files passed. Root reviewed
the complete test and existing producer fixture contracts before approving this
two-file acceptance slice for a protected-main PR. No runtime, API, auth, schema,
persistent data or deployment behavior changes. Quotation issue #73 and Intranet
#241 remain open for their broader acceptance gates; these five tests do not
close whole source commits or claim an authenticated end-to-end browser chain.
